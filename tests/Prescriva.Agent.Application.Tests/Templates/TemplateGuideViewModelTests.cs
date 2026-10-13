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
