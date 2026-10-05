using System.Collections.Immutable;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Desktop.Configuration;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Tests.Configuration;

/// <summary>
/// Exercises IntegrationEditorViewModel against fakes for IConfigurationStore,
/// ISelectorResolver and ICaptureProvider - no real UI Automation or file I/O involved.
/// These prove the workflow orchestration (semantic ID validation, dirty tracking, save
/// validation, reload fidelity, resolve/read) independent of any live desktop, following
/// the same fake-based pattern InspectionControllerTests already established.
/// </summary>
public sealed class IntegrationEditorViewModelTests
{
    private static ApplicationDefinition Application() => new("SomeApp.exe", "Main Window");

    private static ElementFingerprint Fingerprint(string automationId = "MedicationTextBox") =>
        new(
            ProcessIdentity: "SomeApp.exe",
            WindowRule: "Main Window",
            AutomationId: automationId,
            Name: "Medication",
            ControlType: "ControlType.Edit",
            ClassName: "TextBox",
            FrameworkId: "WPF",
            Ancestors: ImmutableArray.Create(new AncestorFingerprint(ControlType: "ControlType.Window")),
            NearbyLabels: ImmutableArray.Create("Medication label"),
            RelativeBounds: new RelativeBounds(0.1, 0.2, 0.3, 0.4));

    private static IntegrationEditorViewModel CreateViewModel(
        out FakeConfigurationStore store,
        out FakeSelectorResolver resolver,
        out FakeCaptureProvider captureProvider)
    {
        store = new FakeConfigurationStore();
        resolver = new FakeSelectorResolver();
        captureProvider = new FakeCaptureProvider();
        return new IntegrationEditorViewModel(store, resolver, captureProvider);
    }

    /// <summary>
    /// Builds a fully valid configuration (at least one stage, field and trigger, as
    /// ConfigurationValidator requires) and returns the exact selector instance used for
    /// the field, so callers can assert on it by reference-safe equality rather than
    /// constructing a second, structurally-identical-but-distinct ElementFingerprint
    /// (ImmutableArray fields inside it compare by underlying array reference, not value).
    /// </summary>
    private static ElementFingerprint AddValidStageAndField(IntegrationEditorViewModel viewModel)
    {
        var selector = Fingerprint();
        viewModel.AddStage("intake", "Intake");
        viewModel.AddField("medication_name", "intake", "Medication name", required: true, selector);
        viewModel.AddTrigger(
            "next_trigger",
            "intake",
            Fingerprint("NextButton"),
            "Invoke",
            ImmutableArray.Create<TriggerActionDefinition>(new FinishSessionAction()));
        return selector;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has spaces")]
    [InlineData("1starts_with_digit")]
    [InlineData("has-dash")]
    [InlineData(null)]
    public void AddField_rejects_invalid_semantic_ids(string? invalidId)
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        viewModel.AddStage("intake", "Intake");

        Assert.Throws<ArgumentException>(() =>
            viewModel.AddField(invalidId!, "intake", "Medication name", required: true, Fingerprint()));

        Assert.Empty(viewModel.Configuration!.Fields);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    public void AddTrigger_rejects_invalid_semantic_ids(string invalidId)
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        viewModel.AddStage("intake", "Intake");

        Assert.Throws<ArgumentException>(() =>
            viewModel.AddTrigger(
                invalidId,
                "intake",
                Fingerprint("NextButton"),
                "Invoke",
                ImmutableArray.Create<TriggerActionDefinition>(new ClearStateAction())));

        Assert.Empty(viewModel.Configuration!.Triggers);
    }

    [Fact]
    public void AddField_accepts_a_valid_semantic_id()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        viewModel.AddStage("intake", "Intake");

        viewModel.AddField("medication_name", "intake", "Medication name", required: true, Fingerprint());

        var field = Assert.Single(viewModel.Configuration!.Fields);
        Assert.Equal("medication_name", field.Id);
    }

    [Fact]
    public void Unsaved_edits_are_visible_through_HasUnsavedChanges()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public async Task HasUnsavedChanges_becomes_false_after_a_successful_save()
    {
        var viewModel = CreateViewModel(out var store, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);
        Assert.True(viewModel.HasUnsavedChanges);

        await viewModel.SaveAsync();

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.Single(store.SavedConfigurations);
    }

    [Fact]
    public async Task SaveAsync_validates_the_configuration_and_does_not_persist_when_invalid()
    {
        var viewModel = CreateViewModel(out var store, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        // No stage/field/trigger added: ConfigurationValidator requires at least one of each.

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => viewModel.SaveAsync());

        Assert.Empty(store.SavedConfigurations);
        Assert.True(viewModel.HasUnsavedChanges);
        Assert.NotEmpty(viewModel.LastValidationErrors);
    }

    [Fact]
    public async Task ReloadAsync_preserves_fingerprints_round_tripped_through_the_store()
    {
        var viewModel = CreateViewModel(out var store, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        var originalFingerprint = AddValidStageAndField(viewModel);
        await viewModel.SaveAsync();

        // A fresh view model, as if the app were restarted, reloading purely from the store.
        var reloaded = new IntegrationEditorViewModel(store, new FakeSelectorResolver(), new FakeCaptureProvider());
        await reloaded.ReloadAsync("integration-1");

        Assert.False(reloaded.HasUnsavedChanges);
        var reloadedField = Assert.Single(reloaded.Configuration!.Fields);
        Assert.Equal(originalFingerprint, reloadedField.Selector);
        Assert.Equal(originalFingerprint.AutomationId, reloadedField.Selector.AutomationId);
        Assert.Equal(originalFingerprint.Ancestors, reloadedField.Selector.Ancestors);
        Assert.Equal(originalFingerprint.RelativeBounds, reloadedField.Selector.RelativeBounds);
    }

    [Fact]
    public async Task ResolveFieldAsync_delegates_to_the_resolver_and_reports_not_found()
    {
        var viewModel = CreateViewModel(out _, out var resolver, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        var selector = AddValidStageAndField(viewModel);
        resolver.NextResult = SelectorResolution.NotFound("no such element");

        var resolution = await viewModel.ResolveFieldAsync("medication_name");

        Assert.Equal(SelectorResolutionStatus.NotFound, resolution.Status);
        Assert.Same(selector, resolver.LastFingerprint);
    }

    [Fact]
    public async Task ReadFieldValueAsync_captures_through_the_provider_after_a_successful_resolve()
    {
        var viewModel = CreateViewModel(out _, out var resolver, out var captureProvider);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);

        var handle = new FakeResolvedElementHandle();
        resolver.NextResult = SelectorResolution.Found(handle, 0.95);
        await viewModel.ResolveFieldAsync("medication_name");

        captureProvider.NextResult = new CaptureResult(
            CaptureOutcome.Captured, "Amoxicillin", CaptureResult.UiaProviderId, 1.0, TimeSpan.FromMilliseconds(5), []);

        var result = await viewModel.ReadFieldValueAsync("medication_name");

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.Equal("Amoxicillin", result.Value);
        Assert.Same(handle, captureProvider.LastHandle);
    }

    [Fact]
    public async Task ReadFieldValueAsync_throws_when_the_field_has_not_been_resolved_yet()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.ReadFieldValueAsync("medication_name"));
    }

    [Fact]
    public async Task RemoveField_removes_an_unreferenced_field_and_marks_the_configuration_unsaved()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);
        viewModel.AddField("note", "intake", "Note", required: false, Fingerprint("NoteTextBox"));
        await viewModel.SaveAsync();

        viewModel.RemoveField("note");

        Assert.DoesNotContain(viewModel.Configuration!.Fields, field => field.Id == "note");
        Assert.True(viewModel.HasUnsavedChanges);
    }

    [Fact]
    public void RemoveField_is_refused_while_a_trigger_captures_it_and_names_that_trigger()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);
        viewModel.AddTrigger("add_item", "intake", Fingerprint("AddButton"), "Invoke",
            [new CaptureFieldsAction(["medication_name"]), new EmitEventAction("item_added")]);
        var before = viewModel.Configuration;

        var error = Assert.Throws<InvalidOperationException>(() => viewModel.RemoveField("medication_name"));

        Assert.Contains("add_item", error.Message, StringComparison.Ordinal);
        Assert.Same(before, viewModel.Configuration);
    }

    [Fact]
    public async Task RemoveField_forgets_a_resolved_handle_so_a_re_added_field_must_be_resolved_again()
    {
        var viewModel = CreateViewModel(out _, out var resolver, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);
        viewModel.AddField("note", "intake", "Note", required: false, Fingerprint("NoteTextBox"));
        resolver.NextResult = SelectorResolution.Found(new FakeResolvedElementHandle(), 0.95);
        await viewModel.ResolveFieldAsync("note");

        viewModel.RemoveField("note");
        viewModel.AddField("note", "intake", "Note", required: false, Fingerprint("OtherNoteTextBox"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.ReadFieldValueAsync("note"));
    }

    [Fact]
    public void UpdateField_changes_meaning_and_required_but_keeps_the_selector()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        var selector = AddValidStageAndField(viewModel);

        viewModel.UpdateField("medication_name", "Nome do medicamento", required: false);

        var field = Assert.Single(viewModel.Configuration!.Fields);
        Assert.Equal("Nome do medicamento", field.Meaning);
        Assert.False(field.Required);
        Assert.Same(selector, field.Selector);
    }

    [Fact]
    public void RemoveTrigger_and_ReplaceTriggerActions_edit_the_named_trigger_only()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);
        viewModel.AddTrigger("add_item", "intake", Fingerprint("AddButton"), "Invoke",
            [new CaptureFieldsAction(["medication_name"]), new EmitEventAction("item_added")]);

        viewModel.ReplaceTriggerActions("add_item", [new EmitEventAction("item_added"), new ClearStateAction()]);
        viewModel.RemoveTrigger("next_trigger");

        var trigger = Assert.Single(viewModel.Configuration!.Triggers);
        Assert.Equal("add_item", trigger.Id);
        Assert.Collection(
            trigger.Actions,
            action => Assert.IsType<EmitEventAction>(action),
            action => Assert.IsType<ClearStateAction>(action));
    }

    [Fact]
    public void RemoveStage_is_refused_while_a_field_trigger_or_transition_uses_it()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);
        viewModel.AddStage("review", "Review");
        viewModel.ReplaceTriggerActions("next_trigger", [new TransitionStageAction("review")]);

        Assert.Contains("medication_name", Assert.Throws<InvalidOperationException>(() => viewModel.RemoveStage("intake")).Message, StringComparison.Ordinal);
        Assert.Contains("next_trigger", Assert.Throws<InvalidOperationException>(() => viewModel.RemoveStage("review")).Message, StringComparison.Ordinal);

        viewModel.ReplaceTriggerActions("next_trigger", [new FinishSessionAction()]);
        viewModel.RemoveStage("review");
        Assert.DoesNotContain(viewModel.Configuration!.Stages, stage => stage.Id == "review");
    }

    [Fact]
    public void Duplicate_ids_are_refused_on_add()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);

        Assert.Throws<InvalidOperationException>(() => viewModel.AddStage("intake", "Again"));
        Assert.Throws<InvalidOperationException>(() => viewModel.AddField("medication_name", "intake", "Again", required: true, Fingerprint("Other")));
        Assert.Throws<InvalidOperationException>(() => viewModel.AddTrigger("next_trigger", "intake", Fingerprint("Other"), "Invoke", [new FinishSessionAction()]));
    }

    [Fact]
    public void Editing_unknown_ids_is_refused()
    {
        var viewModel = CreateViewModel(out _, out _, out _);
        viewModel.CreateIntegration("integration-1", "Integration 1", Application());
        AddValidStageAndField(viewModel);

        Assert.Throws<InvalidOperationException>(() => viewModel.RemoveField("missing"));
        Assert.Throws<InvalidOperationException>(() => viewModel.UpdateField("missing", "x", required: true));
        Assert.Throws<InvalidOperationException>(() => viewModel.RemoveTrigger("missing"));
        Assert.Throws<InvalidOperationException>(() => viewModel.ReplaceTriggerActions("missing", [new FinishSessionAction()]));
        Assert.Throws<InvalidOperationException>(() => viewModel.RemoveStage("missing"));
    }

    private sealed class FakeConfigurationStore : IConfigurationStore
    {
        public List<IntegrationConfiguration> SavedConfigurations { get; } = [];

        public Task<IntegrationConfiguration> LoadAsync(string id, CancellationToken cancellationToken)
        {
            var found = SavedConfigurations.LastOrDefault(configuration => configuration.Id == id);
            if (found is null) throw new FileNotFoundException($"No configuration saved with ID '{id}'.");
            return Task.FromResult(found);
        }

        public Task SaveAsync(IntegrationConfiguration configuration, CancellationToken cancellationToken)
        {
            SavedConfigurations.Add(configuration);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSelectorResolver : ISelectorResolver
    {
        public SelectorResolution NextResult { get; set; } = SelectorResolution.NotFound();

        public ElementFingerprint? LastFingerprint { get; private set; }

        public Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken)
        {
            LastFingerprint = fingerprint;
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeCaptureProvider : ICaptureProvider
    {
        public CaptureResult NextResult { get; set; } =
            new(CaptureOutcome.UnsupportedPattern, null, CaptureResult.UiaProviderId, 0, TimeSpan.Zero, []);

        public ResolvedElementHandle? LastHandle { get; private set; }

        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken)
        {
            LastHandle = handle;
            return Task.FromResult(NextResult);
        }
    }

    /// <summary>
    /// A test-only concrete handle. Constructible only because
    /// Prescriva.Agent.Application grants this test assembly InternalsVisibleTo access to
    /// ResolvedElementHandle's internal constructor - production code outside
    /// Prescriva.Agent.Windows still cannot construct one.
    /// </summary>
    private sealed class FakeResolvedElementHandle : ResolvedElementHandle;
}
