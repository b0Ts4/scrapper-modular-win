using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Templates;

/// <summary>
/// A ready-made starting point for an integration (plan 15): the program it is meant for and an
/// ordered checklist of fields and buttons, each explained in plain words and optionally with a
/// suggested position. A template is data (JSON), never a fixed integration: the person marks,
/// tests, renames, removes or adds items and may save the result as a template of their own.
/// </summary>
public sealed record IntegrationTemplate(
    string Id,
    string Name,
    string Description,
    TemplateProgram Program,
    ImmutableArray<TemplateItem> Items);

/// <summary>Which program and screen a template is meant for, and how to open that screen.</summary>
public sealed record TemplateProgram(string ProcessIdentity, string WindowTitleContains, string HowToOpen)
{
    /// <summary>True for a window of this program whose title contains <see cref="WindowTitleContains"/> (ignoring case).</summary>
    public bool Matches(string processName, string windowTitle) =>
        string.Equals(Normalize(ProcessIdentity), Normalize(processName), StringComparison.OrdinalIgnoreCase) &&
        windowTitle.Contains(WindowTitleContains, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string process)
    {
        var trimmed = process.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}

public enum TemplateItemKind
{
    /// <summary>A value to read.</summary>
    Field,

    /// <summary>A button whose click reads the fields.</summary>
    Button,
}

/// <summary>
/// One checklist entry. <see cref="Help"/> says what it is and where to find it.
/// <see cref="Suggested"/> is a position to try first (its process and window are replaced by
/// the chosen program's); without it the person marks the item. A button reads
/// <see cref="CapturedFields"/>, or every field when none are listed.
/// </summary>
public sealed record TemplateItem(
    string Id,
    TemplateItemKind Kind,
    string Name,
    string Help,
    FieldKind FieldKind = FieldKind.Text,
    bool Required = true,
    ElementFingerprint? Suggested = null,
    ImmutableArray<string> CapturedFields = default,
    string? EmitEvent = "item_added")
{
    /// <summary>The suggested position aimed at <paramref name="application"/>, or null when there is none.</summary>
    public ElementFingerprint? SuggestionFor(ApplicationDefinition application)
    {
        ArgumentNullException.ThrowIfNull(application);
        return Suggested is null
            ? null
            : Suggested with { ProcessIdentity = application.ProcessIdentity, WindowRule = application.WindowRule };
    }
}

/// <summary>Checks a template before it is offered or saved; returns readable problems (empty when valid).</summary>
public static class TemplateValidator
{
    private static readonly Regex SemanticId = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static IReadOnlyList<string> Validate(IntegrationTemplate? template)
    {
        if (template is null)
        {
            return ["O modelo está vazio."];
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(template.Name))
        {
            errors.Add("O modelo precisa de um nome.");
        }

        if (template.Program is null || string.IsNullOrWhiteSpace(template.Program.ProcessIdentity))
        {
            errors.Add("O modelo precisa dizer para qual programa é.");
        }

        if (template.Items.IsDefaultOrEmpty)
        {
            errors.Add("O modelo precisa de pelo menos um item.");
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in template.Items)
        {
            if (item is not { Id: { } id } || !SemanticId.IsMatch(id))
            {
                errors.Add($"O item '{item?.Id}' precisa de um identificador com letras sem acento, números e _ (começando por letra).");
                continue;
            }

            if (!seen.Add(id))
            {
                errors.Add($"O item '{id}' aparece mais de uma vez.");
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                errors.Add($"O item '{id}' precisa de um nome.");
            }
        }

        var fieldIds = template.Items.Where(item => item?.Kind == TemplateItemKind.Field).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var button in template.Items.Where(item => item?.Kind == TemplateItemKind.Button && !item.CapturedFields.IsDefaultOrEmpty))
        {
            foreach (var missing in button.CapturedFields.Where(id => !fieldIds.Contains(id)))
            {
                errors.Add($"O botão '{button.Id}' lê o campo '{missing}', que não está no modelo.");
            }
        }

        return errors;
    }
}

/// <summary>Where templates come from: the ones shipped with the Agent and the person's own.</summary>
public interface ITemplateStore
{
    /// <summary>Every valid template, shipped first; an own template replaces a shipped one with the same ID.</summary>
    Task<IReadOnlyList<StoredTemplate>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Saves an own template (validated first).</summary>
    Task SaveAsync(IntegrationTemplate template, CancellationToken cancellationToken);
}

public sealed record StoredTemplate(IntegrationTemplate Template, bool IsBuiltIn);
