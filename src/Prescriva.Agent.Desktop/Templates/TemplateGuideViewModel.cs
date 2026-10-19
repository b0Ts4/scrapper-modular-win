using System.Collections.Immutable;
using System.Globalization;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Templates;
using Prescriva.Agent.Desktop.Configuration;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Desktop.Templates;

public enum GuideItemStatus
{
    ToDo,
    Marked,
    Passed,
    Failed,
    Skipped,
}

/// <summary>One line of the checklist: the template item as the person has adjusted it, and how far it got.</summary>
public sealed class GuideItem
{
    internal GuideItem(TemplateItem item)
    {
        Id = item.Id;
        Kind = item.Kind;
        Name = item.Name;
        Help = item.Help;
        FieldKind = item.FieldKind;
        Required = item.Required;
        Suggested = item.Suggested;
        CapturedFields = item.CapturedFields;
        EmitEvent = item.EmitEvent;
        Program = item.Program;
        FileSource = item.FileSource;
        Role = item.Role;
        Observe = item.Observe;
        UniqueBy = item.UniqueBy;
    }

    /// <summary>What starts a button item: a click, or a window of the program appearing (plan 17).</summary>
    public TemplateTriggerEvent Observe { get; }

    /// <summary>Fields whose unchanged values keep the button from sending again (plan 17), or none.</summary>
    public ImmutableArray<string> UniqueBy { get; }

    private bool IsWindow => Observe == TemplateTriggerEvent.WindowShown;

    /// <summary>The program of an item that lives outside the integration's main program (plan 16), or null.</summary>
    public TemplateProgram? Program { get; }

    public FileSource? FileSource { get; }

    public TemplateButtonRole Role { get; }

    public string Id { get; }

    public TemplateItemKind Kind { get; }

    public string Name { get; internal set; }

    public string Help { get; }

    public FieldKind FieldKind { get; internal set; }

    public bool Required { get; internal set; }

    public ElementFingerprint? Suggested { get; }

    public ImmutableArray<string> CapturedFields { get; }

    public string? EmitEvent { get; }

    /// <summary>The position in use once marked (the suggestion or one marked by hand).</summary>
    public ElementFingerprint? Selector { get; internal set; }

    public GuideItemStatus Status { get; internal set; } = GuideItemStatus.ToDo;

    public string LastResult { get; internal set; } = string.Empty;

    public bool HasSuggestion => Suggested is not null;

    public string KindText => Kind == TemplateItemKind.Button
        ? (IsWindow ? "pergunta que aparece" : "botão") + Role switch
        {
            TemplateButtonRole.CaptureOnly => " (guarda)",
            TemplateButtonRole.Discard => " (descarta)",
            TemplateButtonRole.SendAndKeep => " (envia e mantém)",
            _ => " (envia)",
        }
        : FieldKind switch
        {
            FieldKind.OcrText => "texto via OCR",
            FieldKind.ScreenImage => "imagem da tela",
            FieldKind.File => "arquivo / imagem",
            FieldKind.RecentFile => "arquivo aberto pelo programa",
            _ => "texto",
        };

    public string StatusText => Status switch
    {
        GuideItemStatus.Marked => "marcado - falta testar",
        GuideItemStatus.Passed => "testado ✓",
        GuideItemStatus.Failed => "falhou ✗",
        GuideItemStatus.Skipped => "pulado",
        _ => "a fazer",
    };

    /// <summary>The list line (also its accessible name).</summary>
    public override string ToString() => $"{Name} · {KindText} · {StatusText}";
}

/// <summary>
/// The guided checklist of a template (plan 15). Starting it creates the integration; each
/// item is then marked - with its suggested position or by hand -, tested on its own,
/// skipped or adjusted, and the integration being edited follows every change. Buttons read
/// the fields they list (or all) that are marked. The result can be saved as the person's
/// own template. Holds no values: a test result is shown, never kept beyond the last line.
/// </summary>
public sealed class TemplateGuideViewModel
{
    public const string StageId = "principal";

    private readonly IntegrationEditorViewModel _editor;
    private readonly Func<TriggerDefinition, CancellationToken, Task<bool>> _waitForClick;
    private readonly List<GuideItem> _items = [];
    private Func<TemplateProgram, ApplicationDefinition?> _locate = _ => null;

    /// <param name="waitForClick">Watches one button until it is clicked once (true) or the wait ends (false).</param>
    public TemplateGuideViewModel(IntegrationEditorViewModel editor, Func<TriggerDefinition, CancellationToken, Task<bool>> waitForClick)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(waitForClick);
        _editor = editor;
        _waitForClick = waitForClick;
    }

    public IntegrationTemplate? Template { get; private set; }

    public bool IsActive => Template is not null;

    public IReadOnlyList<GuideItem> Items => _items;

    public int DoneCount => _items.Count(item => item.Status is GuideItemStatus.Passed or GuideItemStatus.Skipped);

    /// <summary>Creates the integration for <paramref name="application"/> and lists every item as to do.</summary>
    /// <param name="locate">Finds the open window of another program an item lives in (plan 16), or null when it is not open.</param>
    public void Start(IntegrationTemplate template, ApplicationDefinition application, string integrationId,
        Func<TemplateProgram, ApplicationDefinition?>? locate = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(application);
        var errors = TemplateValidator.Validate(template);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(template));
        }

        _editor.CreateIntegration(integrationId, template.Name, application);
        _editor.AddStage(StageId, "Principal");
        Template = template;
        _locate = locate ?? (_ => null);
        _items.Clear();
        // An item of another program is aimed at that program's window when it is used (it may open later).
        _items.AddRange(template.Items.Select(item => new GuideItem(
            item.Program is null ? item with { Suggested = item.SuggestionFor(application) } : item)));
    }

    /// <summary>Leaves the checklist (another integration was created or opened); the integration itself is untouched.</summary>
    public void Reset()
    {
        Template = null;
        _items.Clear();
    }

    public GuideItem Item(string id) =>
        _items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"O roteiro não tem o item '{id}'.");

    /// <summary>Marks the item with its suggested position.</summary>
    public void UseSuggestion(string id)
    {
        var item = Item(id);
        if (item.Suggested is null)
        {
            throw new InvalidOperationException($"'{item.Name}' não tem posição sugerida: marque-o no programa.");
        }

        if (item.Program is { } program)
        {
            var window = _locate(program)
                ?? throw new InvalidOperationException($"A janela '{program.WindowTitleContains}' não está aberta. {program.HowToOpen}");
            Mark(id, item.Suggested with { ProcessIdentity = window.ProcessIdentity, WindowRule = window.WindowRule });
            return;
        }

        Mark(id, item.Suggested);
    }

    /// <summary>Marks the item with a position (marked by hand or suggested); the integration follows.</summary>
    public void Mark(string id, ElementFingerprint selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var item = Item(id);
        var configuration = RequireConfiguration();
        if (item.Observe == TemplateTriggerEvent.WindowShown && item.Suggested is { } question)
        {
            // A question is told by its window class and the start of its title, not by what was
            // pointed at (a button in it, or the whole title with this quote's number).
            selector = selector with { ClassName = question.ClassName, Name = question.Name, AutomationId = null, WindowPath = null };
        }

        if (item.Kind == TemplateItemKind.Field)
        {
            if (configuration.Fields.Any(field => field.Id == item.Id))
            {
                _editor.SetFieldSelector(item.Id, selector);
            }
            else
            {
                _editor.AddField(item.Id, StageId, item.Name, item.Required, selector, item.FieldKind, item.FileSource);
            }
        }
        else if (configuration.Triggers.Any(trigger => trigger.Id == item.Id))
        {
            _editor.SetTriggerSelector(item.Id, selector);
        }
        else
        {
            _editor.AddTrigger(item.Id, StageId, selector,
                item.Observe == TemplateTriggerEvent.WindowShown ? TriggerEvents.WindowShown : TriggerEvents.Invoke,
                ActionsFor(item));
        }

        item.Selector = selector;
        item.Status = GuideItemStatus.Marked;
        item.LastResult = item.Kind != TemplateItemKind.Button
            ? "Marcado. Clique em 'Testar este item' para ver o que é lido."
            : item.Observe == TemplateTriggerEvent.WindowShown
                ? "Marcado. Clique em 'Testar este item' e faça a pergunta aparecer no programa."
                : "Marcado. Clique em 'Testar este item' e depois clique o botão no programa.";
        RebuildButtons();
    }

    /// <summary>Tests one item: a field is found and read; a button must be clicked once in the program.</summary>
    public async Task TestAsync(string id, CancellationToken cancellationToken = default)
    {
        var item = Item(id);
        if (item.Selector is null || item.Status is GuideItemStatus.ToDo or GuideItemStatus.Skipped)
        {
            item.LastResult = "Marque este item antes de testá-lo (posição sugerida ou 'Marcar no programa').";
            return;
        }

        if (item.Kind == TemplateItemKind.Button)
        {
            var trigger = RequireConfiguration().Triggers.Single(candidate => candidate.Id == item.Id);
            var clicked = await _waitForClick(trigger, cancellationToken).ConfigureAwait(true);
            if (item.Observe == TemplateTriggerEvent.WindowShown)
            {
                Record(item, clicked, clicked ? "Pergunta detectada." : "A pergunta não apareceu no tempo de espera. Teste de novo e faça-a aparecer no programa.");
                return;
            }

            Record(item, clicked, clicked ? "Clique detectado." : "Nenhum clique detectado no tempo de espera. Teste de novo e clique o botão no programa.");
            return;
        }

        var resolution = await _editor.ResolveFieldAsync(item.Id, cancellationToken).ConfigureAwait(true);
        if (resolution.Status != SelectorResolutionStatus.Found)
        {
            Record(item, false, resolution.Status switch
            {
                SelectorResolutionStatus.WindowMissing => "A janela do programa não foi encontrada: deixe a tela certa aberta.",
                SelectorResolutionStatus.Ambiguous => "Mais de um elemento parecido: marque o campo no programa.",
                SelectorResolutionStatus.TimedOut => "O programa demorou demais para responder. Tente de novo.",
                _ => "O campo não foi encontrado nesta tela: marque-o no programa.",
            });
            return;
        }

        var result = await _editor.ReadFieldValueAsync(item.Id, cancellationToken).ConfigureAwait(true);
        Record(item, result.Outcome == CaptureOutcome.Captured, Describe(result, resolution.Confidence));
    }

    /// <summary>Leaves the item out: if it was marked, it is taken out of the integration (and of the buttons).</summary>
    public void Skip(string id)
    {
        var item = Item(id);
        Unmark(item);
        item.Status = GuideItemStatus.Skipped;
        item.LastResult = "Pulado: não faz parte da integração.";
    }

    /// <summary>Changes the item's name, how it is read and whether it is required.</summary>
    public void Adjust(string id, string name, FieldKind kind, bool required)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var item = Item(id);
        var kindChanged = item.FieldKind != kind;
        item.Name = name.Trim();
        item.FieldKind = kind;
        item.Required = required;

        if (item.Kind == TemplateItemKind.Field && RequireConfiguration().Fields.Any(field => field.Id == item.Id))
        {
            _editor.UpdateField(item.Id, item.Name, required);
            _editor.SetFieldKind(item.Id, kind);
            if (kindChanged && item.Status == GuideItemStatus.Passed)
            {
                item.Status = GuideItemStatus.Marked;
                item.LastResult = "Tipo alterado: teste de novo.";
            }
        }
    }

    /// <summary>Adds an item of the person's own to the end of the checklist.</summary>
    public GuideItem AddItem(string name, TemplateItemKind kind, FieldKind fieldKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = SemanticIdGenerator.FromName(name, _items.Select(item => item.Id), kind == TemplateItemKind.Button ? "botao" : "campo");
        var item = new GuideItem(new TemplateItem(id, kind, name.Trim(), "Item criado por você: marque-o no programa.", fieldKind, Required: false));
        _items.Add(item);
        return item;
    }

    /// <summary>Removes an item from the checklist (and from the integration, if marked).</summary>
    public void RemoveItem(string id)
    {
        var item = Item(id);
        Unmark(item);
        _items.Remove(item);
    }

    /// <summary>The checklist as it is now, as a template of the person's own: skipped items left out, marked positions kept as suggestions.</summary>
    public IntegrationTemplate ToTemplate(string id, string name)
    {
        var template = Template ?? throw new InvalidOperationException("Comece por um modelo antes de salvá-lo.");
        var kept = _items.Where(item => item.Status != GuideItemStatus.Skipped).ToArray();
        var fieldIds = kept.Where(item => item.Kind == TemplateItemKind.Field).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return template with
        {
            Id = id,
            Name = name,
            Items = kept.Select(item => new TemplateItem(
                item.Id,
                item.Kind,
                item.Name,
                item.Help,
                item.FieldKind,
                item.Required,
                (item.Selector ?? item.Suggested) is { } position ? position with { ProcessIdentity = string.Empty, WindowRule = string.Empty } : null,
                item.CapturedFields.IsDefaultOrEmpty ? default : item.CapturedFields.Where(fieldIds.Contains).ToImmutableArray(),
                item.EmitEvent,
                item.Program,
                item.FileSource,
                item.Role,
                item.Observe,
                item.UniqueBy.IsDefaultOrEmpty || !item.UniqueBy.All(fieldIds.Contains) ? default : item.UniqueBy)).ToImmutableArray(),
        };
    }

    private void Unmark(GuideItem item)
    {
        var configuration = RequireConfiguration();
        if (item.Kind == TemplateItemKind.Button)
        {
            if (configuration.Triggers.Any(trigger => trigger.Id == item.Id))
            {
                _editor.RemoveTrigger(item.Id);
            }
        }
        else if (configuration.Fields.Any(field => field.Id == item.Id))
        {
            item.Selector = null;
            item.Status = GuideItemStatus.ToDo;
            RebuildButtons(exclude: item.Id);
            _editor.RemoveField(item.Id);
        }

        item.Selector = null;
    }

    /// <summary>Every marked button reads the marked fields it lists (or all of them), then emits its event and clears.</summary>
    private void RebuildButtons(string? exclude = null)
    {
        foreach (var button in _items.Where(item => item.Kind == TemplateItemKind.Button && item.Selector is not null))
        {
            _editor.ReplaceTriggerActions(button.Id, ActionsFor(button, exclude));
        }
    }

    /// <summary>
    /// What a button does, by its role: send = read its fields, emit one event with everything
    /// kept so far, clear; send and keep = the same without clearing; keep = read its fields only;
    /// discard = clear.
    /// </summary>
    private ImmutableArray<TriggerActionDefinition> ActionsFor(GuideItem button, string? exclude = null)
    {
        if (button.Role == TemplateButtonRole.Discard)
        {
            return [new ClearStateAction()];
        }

        var marked = _items
            .Where(item => item.Kind == TemplateItemKind.Field && item.Selector is not null && item.Id != exclude)
            .Where(item => button.CapturedFields.IsDefaultOrEmpty || button.CapturedFields.Contains(item.Id))
            .Select(item => item.Id)
            .ToArray();
        if (button.Role == TemplateButtonRole.CaptureOnly)
        {
            // Nothing marked yet to keep: a trigger needs an action, and clearing would throw away
            // what other buttons kept, so it only stays in the same stage until its fields are marked.
            return marked.Length > 0 ? [new CaptureFieldsAction([.. marked])] : [new TransitionStageAction(StageId)];
        }

        // Not sent again while its unique-by fields keep the values of its last event - only with
        // all of them marked: a narrower key would tell fewer events apart and silently drop some.
        return TriggerActionsBuilder.Build(
            string.Join(",", marked),
            string.IsNullOrWhiteSpace(button.EmitEvent) ? "item_added" : button.EmitEvent,
            transitionStageId: null,
            clearState: marked.Length > 0 && button.Role != TemplateButtonRole.SendAndKeep,
            TriggerTerminalAction.None,
            button.UniqueBy.IsDefaultOrEmpty || !button.UniqueBy.All(marked.Contains) ? null : button.UniqueBy);
    }

    private static void Record(GuideItem item, bool passed, string message)
    {
        item.Status = passed ? GuideItemStatus.Passed : GuideItemStatus.Failed;
        item.LastResult = message;
    }

    private static string Describe(CaptureResult result, double confidence)
    {
        var found = $"Encontrado ({confidence.ToString("P0", CultureInfo.GetCultureInfo("pt-BR"))}). ";
        if (result.Outcome != CaptureOutcome.Captured)
        {
            return found + result.Outcome switch
            {
                CaptureOutcome.Obscured => "Mas está coberto por outra janela ou fora da tela: deixe-o visível.",
                CaptureOutcome.OcrUnavailable => "Mas não há idioma de OCR instalado no Windows.",
                CaptureOutcome.UnsupportedPattern => "Mas o programa não deixa ler este campo como texto: tente o tipo 'Texto via OCR'.",
                CaptureOutcome.TooLarge => "Mas o arquivo passa de 10 MB.",
                CaptureOutcome.FileUnavailable => "Mas o arquivo indicado não pôde ser lido.",
                _ => "Mas não foi possível ler agora. Tente de novo.",
            };
        }

        if (result.Attachment is { } attachment)
        {
            var kilobytes = Math.Round(attachment.Content.LongLength / 1024.0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
            return found + (attachment.Source == AttachmentSource.File
                ? $"Arquivo copiado inteiro ({kilobytes} KB, {attachment.FileName})."
                : $"Imagem capturada ({kilobytes} KB).");
        }

        return string.IsNullOrEmpty(result.Value)
            ? found + "Lido, mas está vazio agora: preencha o campo no programa e teste de novo para ver o valor."
            : found + "Lido: " + result.Value;
    }

    private IntegrationConfiguration RequireConfiguration() =>
        _editor.Configuration ?? throw new InvalidOperationException("Comece por um modelo antes.");
}
