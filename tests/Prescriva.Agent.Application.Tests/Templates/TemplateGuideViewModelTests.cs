using System.Collections.Immutable;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Templates;
using Prescriva.Agent.Desktop.Configuration;
using Prescriva.Agent.Desktop.Templates;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Tests.Templates;

/// <summary>
/// The guided checklist of a template (plan 15): each item is marked (by its suggestion or by
/// hand), tested on its own, skipped or adjusted, and the integration follows.
/// </summary>
public sealed class TemplateGuideViewModelTests
{
    private static readonly ApplicationDefinition Program = new("FCReceitas.exe", "FórmulaCerta - Orçamentos 6.0.1164");

    private readonly FakeResolver _resolver = new();
    private readonly FakeCapture _capture = new();
    private bool _clickSeen = true;

    [Fact]
    public void Starting_creates_the_integration_with_every_item_to_do()
    {
        var (guide, editor) = Start();

        Assert.Equal("FórmulaCerta – Orçamento", editor.Configuration!.Name);
        Assert.Single(editor.Configuration.Stages);
        Assert.Equal(["posologia", "prescritor_nome", "ok"], guide.Items.Select(item => item.Id));
        Assert.All(guide.Items, item => Assert.Equal(GuideItemStatus.ToDo, item.Status));
    }

    [Fact]
    public void Using_the_suggestion_adds_the_field_aimed_at_the_chosen_window()
    {
        var (guide, editor) = Start();

        guide.UseSuggestion("posologia");

        var field = Assert.Single(editor.Configuration!.Fields);
        Assert.Equal("posologia", field.Id);
        Assert.Equal("Posologia", field.Meaning);
        Assert.Equal(Program.WindowRule, field.Selector.WindowRule);
        Assert.Equal("TPanel[0]/TwwDBComboBox[2]", field.Selector.WindowPath);
        Assert.Equal(GuideItemStatus.Marked, guide.Item("posologia").Status);
    }

    [Fact]
    public void An_item_without_a_suggestion_must_be_marked_by_hand()
    {
        var (guide, _) = Start();

        Assert.Throws<InvalidOperationException>(() => guide.UseSuggestion("prescritor_nome"));
    }

    [Fact]
    public void A_button_reads_every_marked_field_and_follows_fields_marked_later()
    {
        var (guide, editor) = Start();
        guide.UseSuggestion("posologia");

        guide.Mark("ok", Selector("TButton[0]"));
        Assert.Equal(["posologia"], Captured(editor, "ok"));

        guide.Mark("prescritor_nome", Selector("TPanel[0]/TEdit[0]"));
        Assert.Equal(["posologia", "prescritor_nome"], Captured(editor, "ok"));
        Assert.Contains(editor.Configuration!.Triggers.Single().Actions, action => action is EmitEventAction { EventType: "item_added" });
    }

    [Fact]
    public void Marking_an_item_again_replaces_its_position()
    {
        var (guide, editor) = Start();
        guide.UseSuggestion("posologia");

        guide.Mark("posologia", Selector("TPanel[0]/TEdit[9]"));

        Assert.Equal("TPanel[0]/TEdit[9]", Assert.Single(editor.Configuration!.Fields).Selector.WindowPath);
    }

    [Fact]
    public async Task Testing_a_field_reads_it_and_says_what_was_read()
    {
        var (guide, _) = Start();
        guide.UseSuggestion("posologia");
        _resolver.Next = SelectorResolution.Found(new FakeHandle(), 1.0);
        _capture.Next = new CaptureResult(CaptureOutcome.Captured, "1 cápsula ao dia", "uia", 1.0, TimeSpan.Zero, []);

        await guide.TestAsync("posologia");

        var item = guide.Item("posologia");
        Assert.Equal(GuideItemStatus.Passed, item.Status);
        Assert.Contains("1 cápsula ao dia", item.LastResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_an_image_field_says_how_big_the_image_is()
    {
        var (guide, _) = Start();
        guide.UseSuggestion("posologia");
        _resolver.Next = SelectorResolution.Found(new FakeHandle(), 1.0);
        _capture.Next = new CaptureResult(CaptureOutcome.Captured, null, "uia", 1.0, TimeSpan.Zero, [],
            new CapturedAttachment(new byte[140_000], "foto.png", "image/png", AttachmentSource.Screen));

        await guide.TestAsync("posologia");

        Assert.Contains("137 KB", guide.Item("posologia").LastResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_field_that_is_not_found_fails_with_a_reason_in_words()
    {
        var (guide, _) = Start();
        guide.UseSuggestion("posologia");
        _resolver.Next = SelectorResolution.NotFound();

        await guide.TestAsync("posologia");

        var item = guide.Item("posologia");
        Assert.Equal(GuideItemStatus.Failed, item.Status);
        Assert.Contains("não foi encontrado", item.LastResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_a_button_waits_for_one_click()
    {
        var (guide, _) = Start();
        guide.Mark("ok", Selector("TButton[0]"));

        await guide.TestAsync("ok");
        Assert.Equal(GuideItemStatus.Passed, guide.Item("ok").Status);

        _clickSeen = false;
        await guide.TestAsync("ok");
        Assert.Equal(GuideItemStatus.Failed, guide.Item("ok").Status);
    }

    [Fact]
    public async Task An_item_not_marked_yet_cannot_be_tested()
    {
        var (guide, _) = Start();

        await guide.TestAsync("posologia");

        Assert.Equal(GuideItemStatus.ToDo, guide.Item("posologia").Status);
        Assert.Contains("Marque", guide.Item("posologia").LastResult, StringComparison.Ordinal);
    }

    [Fact]
    public void Skipping_a_marked_field_takes_it_out_of_the_integration_and_of_the_buttons()
    {
        var (guide, editor) = Start();
        guide.UseSuggestion("posologia");
        guide.Mark("prescritor_nome", Selector("TPanel[0]/TEdit[0]"));
        guide.Mark("ok", Selector("TButton[0]"));

        guide.Skip("posologia");

        Assert.Equal(["prescritor_nome"], editor.Configuration!.Fields.Select(field => field.Id));
        Assert.Equal(["prescritor_nome"], Captured(editor, "ok"));
        Assert.Equal(GuideItemStatus.Skipped, guide.Item("posologia").Status);
    }

    [Fact]
    public void Adjusting_an_item_changes_its_name_kind_and_required_in_the_integration()
    {
        var (guide, editor) = Start();
        guide.UseSuggestion("posologia");

        guide.Adjust("posologia", "Modo de usar", FieldKind.OcrText, required: false);

        var field = Assert.Single(editor.Configuration!.Fields);
        Assert.Equal("Modo de usar", field.Meaning);
        Assert.Equal(FieldKind.OcrText, field.Kind);
        Assert.False(field.Required);
        Assert.Equal(GuideItemStatus.Marked, guide.Item("posologia").Status);
    }

    [Fact]
    public void A_new_item_gets_an_id_from_its_name_and_starts_to_do()
    {
        var (guide, _) = Start();

        var item = guide.AddItem("Data da prescrição", TemplateItemKind.Field, FieldKind.Text);

        Assert.Equal("data_da_prescricao", item.Id);
        Assert.Equal(GuideItemStatus.ToDo, item.Status);
        Assert.Equal(4, guide.Items.Count);
    }

    [Fact]
    public void Saving_as_an_own_template_keeps_the_marked_positions_and_the_adjustments()
    {
        var (guide, _) = Start();
        guide.Mark("prescritor_nome", Selector("TPanel[0]/TEdit[0]"));
        guide.Adjust("prescritor_nome", "Médico", FieldKind.Text, required: true);
        guide.Skip("posologia");

        var template = guide.ToTemplate("meu_orcamento", "Meu orçamento");

        Assert.Empty(TemplateValidator.Validate(template));
        Assert.Equal(["prescritor_nome", "ok"], template.Items.Select(item => item.Id));
        var doctor = template.Items[0];
        Assert.Equal("Médico", doctor.Name);
        Assert.Equal("TPanel[0]/TEdit[0]", doctor.Suggested!.WindowPath);
        Assert.Equal(string.Empty, doctor.Suggested.WindowRule);
    }

    /// <summary>
    /// Plan 16: the attachment lives in another program. Plan 18: its suggestion is aimed at that
    /// program by the part of the title the template gives - version-proof, and set up even while
    /// that window is closed (it is checked once it opens).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_item_of_another_program_is_aimed_at_that_program_by_its_title_part_and_keeps_its_file_source(bool viewerOpen)
    {
        var (guide, editor) = StartTwoPrograms(viewerOpen);

        guide.UseSuggestion("anexo");

        var field = editor.Configuration!.Fields.Single(candidate => candidate.Id == "anexo");
        Assert.Equal("FCMobyPharma.exe", field.Selector.ProcessIdentity);
        Assert.Equal("Anexos", field.Selector.WindowRule);
        Assert.Equal(FieldKind.RecentFile, field.Kind);
        Assert.Equal(new FileSource("%LOCALAPPDATA%\\Temp", "anexo_*", 120), field.FileSource);
    }

    /// <summary>Plan 18: one click marks every item the template suggests - fields and triggers alike.</summary>
    [Fact]
    public void Using_every_suggestion_marks_all_suggested_fields_and_triggers_and_names_the_rest()
    {
        var (guide, editor) = StartTwoPrograms(viewerOpen: false);
        guide.Item("posologia"); // has no suggestion in this template

        var left = guide.UseAllSuggestions();

        Assert.Equal(["anexo"], editor.Configuration!.Fields.Select(field => field.Id).ToArray());
        Assert.Equal(["nova_formula", "ok", "cancelar"], editor.Configuration.Triggers.Select(trigger => trigger.Id).ToArray());
        Assert.Equal(["posologia"], left.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void Button_roles_keep_send_or_discard_what_was_captured()
    {
        var (guide, editor) = StartTwoPrograms();
        guide.UseSuggestion("anexo");
        guide.Mark("posologia", Selector("TPanel[0]/TEdit[1]"));

        guide.Mark("nova_formula", Selector("TButton[1]") with { ProcessIdentity = "FCMobyPharma.exe" });
        guide.Mark("ok", Selector("TButton[2]"));
        guide.Mark("cancelar", Selector("TButton[3]"));

        var actions = editor.Configuration!.Triggers.ToDictionary(trigger => trigger.Id, trigger => trigger.Actions);
        Assert.Equal(["anexo"], Assert.IsType<CaptureFieldsAction>(Assert.Single(actions["nova_formula"])).FieldIds.ToArray());
        Assert.Equal(["posologia"], Assert.IsType<CaptureFieldsAction>(actions["ok"][0]).FieldIds.ToArray());
        Assert.Equal("receita", Assert.IsType<EmitEventAction>(actions["ok"][1]).EventType);
        Assert.IsType<ClearStateAction>(actions["ok"][2]);
        Assert.IsType<ClearStateAction>(Assert.Single(actions["cancelar"]));
    }

    [Fact]
    public void A_question_that_appears_becomes_a_window_trigger_sending_each_formula_once()
    {
        var program = IntegrationTemplateTests.Sample().Program;
        var template = new IntegrationTemplate("orcamento", "Orçamento", "x", program,
        [
            new TemplateItem("numero", TemplateItemKind.Field, "Número", "x", Required: false,
                Suggested: new ElementFingerprint(string.Empty, string.Empty, ClassName: "TwwDBEdit", WindowPath: "TFagronGroupBox[1]/TwwDBEdit[1]")),
            new TemplateItem("mais_formulas", TemplateItemKind.Button, "Pergunta 'Mais Fórmula(s)?'", "x",
                Suggested: new ElementFingerprint(string.Empty, string.Empty, Name: "Orçamento ", ClassName: "TMessageForm", FrameworkId: "Win32"),
                EmitEvent: "receita", Role: TemplateButtonRole.SendAndKeep,
                Observe: TemplateTriggerEvent.WindowShown, UniqueBy: ["numero"]),
        ]);
        var editor = new IntegrationEditorViewModel(new FakeStore(), _resolver, _capture);
        var guide = new TemplateGuideViewModel(editor, (_, _) => Task.FromResult(_clickSeen));
        guide.Start(template, Program, "orcamento_teste");

        guide.UseSuggestion("numero");
        guide.UseSuggestion("mais_formulas");

        var trigger = editor.Configuration!.Triggers.Single();
        Assert.Equal(TriggerEvents.WindowShown, trigger.ObservedEvent);
        Assert.Equal("TMessageForm", trigger.Selector.ClassName);
        Assert.Equal("Orçamento ", trigger.Selector.Name);
        var emit = Assert.IsType<EmitEventAction>(trigger.Actions[1]);
        Assert.Equal(["numero"], emit.UniqueBy!.Value.ToArray());
        Assert.Equal(2, trigger.Actions.Length);
        Assert.Equal("pergunta que aparece (envia e mantém)", guide.Item("mais_formulas").KindText);
        var saved = guide.ToTemplate("meu", "Meu").Items.Single(item => item.Id == "mais_formulas");
        Assert.Equal(TemplateTriggerEvent.WindowShown, saved.Observe);
        Assert.Equal(["numero"], saved.UniqueBy.ToArray());
    }

    /// <summary>
    /// A key with one of its fields skipped would tell fewer events apart (every formula of a
    /// quote by its number alone): then nothing is left out - each question sends.
    /// </summary>
    [Fact]
    public void Skipping_a_field_of_the_unique_key_sends_every_time_instead_of_narrowing_the_key()
    {
        var (guide, editor) = StartQuestion();
        guide.UseSuggestion("numero");
        guide.UseSuggestion("serie");
        guide.UseSuggestion("mais_formulas");

        guide.Skip("serie");

        var emit = editor.Configuration!.Triggers.Single().Actions.OfType<EmitEventAction>().Single();
        Assert.Null(emit.UniqueBy);
    }

    /// <summary>A question marked by hand (its window or a button in it) keeps the suggested window class and title start.</summary>
    [Fact]
    public void A_question_marked_by_hand_keeps_the_window_class_and_title_start_of_its_suggestion()
    {
        var (guide, editor) = StartQuestion();

        guide.Mark("mais_formulas", Selector("TMessageForm[0]/TButton[0]") with { ClassName = "TButton", Name = "Sim" });

        var trigger = editor.Configuration!.Triggers.Single();
        Assert.Equal("TMessageForm", trigger.Selector.ClassName);
        Assert.Equal("Orçamento ", trigger.Selector.Name);
        Assert.Null(trigger.Selector.WindowPath);
    }

    private (TemplateGuideViewModel Guide, IntegrationEditorViewModel Editor) StartQuestion()
    {
        var template = new IntegrationTemplate("orcamento", "Orçamento", "x", IntegrationTemplateTests.Sample().Program,
        [
            new TemplateItem("numero", TemplateItemKind.Field, "Número", "x", Required: false,
                Suggested: new ElementFingerprint(string.Empty, string.Empty, ClassName: "TwwDBEdit", WindowPath: "TFagronGroupBox[1]/TwwDBEdit[1]")),
            new TemplateItem("serie", TemplateItemKind.Field, "Série", "x", Required: false,
                Suggested: new ElementFingerprint(string.Empty, string.Empty, ClassName: "TwwDBEdit", WindowPath: "TFagronGroupBox[1]/TwwDBEdit[0]")),
            new TemplateItem("mais_formulas", TemplateItemKind.Button, "Pergunta", "x",
                Suggested: new ElementFingerprint(string.Empty, string.Empty, Name: "Orçamento ", ClassName: "TMessageForm", FrameworkId: "Win32"),
                EmitEvent: "receita", Role: TemplateButtonRole.SendAndKeep,
                Observe: TemplateTriggerEvent.WindowShown, UniqueBy: ["numero", "serie"]),
        ]);
        var editor = new IntegrationEditorViewModel(new FakeStore(), _resolver, _capture);
        var guide = new TemplateGuideViewModel(editor, (_, _) => Task.FromResult(_clickSeen));
        guide.Start(template, Program, "orcamento_teste");
        return (guide, editor);
    }

    [Fact]
    public void A_send_and_keep_button_sends_each_formula_and_keeps_the_prescription_file_for_the_next()
    {
        var (guide, editor) = StartTwoPrograms(okRole: TemplateButtonRole.SendAndKeep);
        guide.UseSuggestion("anexo");
        guide.Mark("posologia", Selector("TPanel[0]/TEdit[1]"));

        guide.Mark("ok", Selector("TButton[2]"));

        var actions = editor.Configuration!.Triggers.Single(trigger => trigger.Id == "ok").Actions;
        Assert.Equal(2, actions.Length);
        Assert.Equal(["posologia"], Assert.IsType<CaptureFieldsAction>(actions[0]).FieldIds.ToArray());
        Assert.Equal("receita", Assert.IsType<EmitEventAction>(actions[1]).EventType);
        Assert.Equal("botão (envia e mantém)", guide.Item("ok").KindText);
    }

    [Fact]
    public async Task Testing_a_file_field_names_the_copied_file()
    {
        var (guide, _) = StartTwoPrograms();
        guide.UseSuggestion("anexo");
        _resolver.Next = SelectorResolution.Found(new FakeHandle(), 1.0);
        _capture.Next = new CaptureResult(CaptureOutcome.Captured, null, "uia", 1.0, TimeSpan.Zero, [],
            new CapturedAttachment(new byte[54_713], "anexo_20261018_183315390.pdf", "application/pdf", AttachmentSource.File));

        await guide.TestAsync("anexo");

        Assert.Contains("anexo_20261018_183315390.pdf", guide.Item("anexo").LastResult, StringComparison.Ordinal);
        Assert.Contains("53 KB", guide.Item("anexo").LastResult, StringComparison.Ordinal);
    }

    private (TemplateGuideViewModel Guide, IntegrationEditorViewModel Editor) StartTwoPrograms(bool viewerOpen = true, TemplateButtonRole okRole = TemplateButtonRole.CaptureAndSend)
    {
        var viewer = new TemplateProgram("FCMobyPharma.exe", "Anexos", "Abra o anexo.");
        var template = new IntegrationTemplate("receita_completa", "Receita completa", "x", IntegrationTemplateTests.Sample().Program,
        [
            new TemplateItem("anexo", TemplateItemKind.Field, "Anexo", "O arquivo da receita.", FieldKind.RecentFile, Required: false,
                Suggested: new ElementFingerprint(string.Empty, string.Empty, ClassName: "TScrollBox", WindowPath: "TPanel[0]/TScrollBox[0]"),
                Program: viewer, FileSource: new FileSource("%LOCALAPPDATA%\\Temp", "anexo_*", 120)),
            new TemplateItem("posologia", TemplateItemKind.Field, "Posologia", "x"),
            new TemplateItem("nova_formula", TemplateItemKind.Button, "Nova Fórmula", "x", CapturedFields: ["anexo"], Program: viewer, Role: TemplateButtonRole.CaptureOnly,
                Suggested: new ElementFingerprint(string.Empty, string.Empty, ClassName: "TFagronButton", WindowPath: "TFagronPanel[0]/TFagronButton[2]")),
            new TemplateItem("ok", TemplateItemKind.Button, "OK", "x", CapturedFields: ["posologia"], EmitEvent: "receita", Role: okRole,
                Suggested: new ElementFingerprint(string.Empty, string.Empty, ClassName: "TFagronButton", WindowPath: "TFagronButton[8]")),
            new TemplateItem("cancelar", TemplateItemKind.Button, "Cancelar", "x", Role: TemplateButtonRole.Discard,
                Suggested: new ElementFingerprint(string.Empty, string.Empty, ClassName: "TFagronButton", WindowPath: "TFagronButton[7]")),
        ]);
        var editor = new IntegrationEditorViewModel(new FakeStore(), _resolver, _capture);
        var guide = new TemplateGuideViewModel(editor, (_, _) => Task.FromResult(_clickSeen));
        guide.Start(template, Program, "receita_teste",
            program => viewerOpen && program.Matches("FCMobyPharma", "Visualização de Anexos")
                ? new ApplicationDefinition("FCMobyPharma.exe", "Visualização de Anexos")
                : null);
        return (guide, editor);
    }

    private (TemplateGuideViewModel Guide, IntegrationEditorViewModel Editor) Start()
    {
        var editor = new IntegrationEditorViewModel(new FakeStore(), _resolver, _capture);
        var guide = new TemplateGuideViewModel(editor, (_, _) => Task.FromResult(_clickSeen));
        guide.Start(IntegrationTemplateTests.Sample() with { Items = IntegrationTemplateTests.Sample().Items.SetItem(2,
            IntegrationTemplateTests.Sample().Items[2] with { CapturedFields = default }) }, Program, "orcamento_teste");
        return (guide, editor);
    }

    private static ElementFingerprint Selector(string path) =>
        new("FCReceitas.exe", Program.WindowRule, ClassName: "TEdit", FrameworkId: "Win32", WindowPath: path);

    private static string[] Captured(IntegrationEditorViewModel editor, string triggerId) =>
        editor.Configuration!.Triggers.Single(trigger => trigger.Id == triggerId)
            .Actions.OfType<CaptureFieldsAction>().SelectMany(action => action.FieldIds).ToArray();

    private sealed class FakeStore : IConfigurationStore
    {
        public Task<IntegrationConfiguration> LoadAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SaveAsync(IntegrationConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeResolver : ISelectorResolver
    {
        public SelectorResolution Next { get; set; } = SelectorResolution.NotFound();

        public Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken) => Task.FromResult(Next);
    }

    private sealed class FakeCapture : ICaptureProvider
    {
        public CaptureResult Next { get; set; } = new(CaptureOutcome.UnsupportedPattern, null, "uia", 0, TimeSpan.Zero, []);

        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken) => Task.FromResult(Next);
    }

    private sealed class FakeHandle : ResolvedElementHandle;
}
