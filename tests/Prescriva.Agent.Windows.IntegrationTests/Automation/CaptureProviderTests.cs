using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// Exercises UiAutomationCaptureProvider against a real, launched
/// Prescriva.Agent.TestTarget process, resolving elements through
/// UiAutomationSelectorResolver first so capture always runs against a genuine, opaque
/// ResolvedElementHandle - never a mock or fake.
/// </summary>
public sealed class CaptureProviderTests
{
    [Fact]
    public async Task CaptureAsync_reads_a_TextBox_value_via_ValuePattern()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();

        var medicationBox = FindById(target, "MedicationTextBox");
        GetValuePattern(medicationBox).SetValue("Amoxicillin");

        var (resolver, provider) = CreateResolverAndProvider(dispatcher);
        var fingerprint = BuildFingerprint(target, "MedicationTextBox", "ControlType.Edit");
        var handle = await ResolveOrFail(resolver, fingerprint);

        var result = await provider.CaptureAsync(handle, BuildField(fingerprint), CancellationToken.None);

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.Equal("Amoxicillin", result.Value);
        Assert.Equal(CaptureResult.UiaProviderId, result.ProviderId);
        Assert.Contains(result.Attempts, a => a is { PatternName: "ValuePattern", Succeeded: true });
    }

    [Fact]
    public async Task CaptureAsync_reads_a_read_only_TextBlock_via_TextPattern_when_ValuePattern_is_unsupported()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();

        var finishButton = FindById(target, "FinishButton");
        GetInvokePattern(finishButton).Invoke();

        var (resolver, provider) = CreateResolverAndProvider(dispatcher);
        var fingerprint = BuildFingerprint(target, "CompletionStatusText", "ControlType.Document");
        var handle = await ResolveOrFail(resolver, fingerprint);

        var result = await provider.CaptureAsync(handle, BuildField(fingerprint), CancellationToken.None);

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.Contains("Completed", result.Value);
        Assert.Contains(result.Attempts, a => a is { PatternName: "ValuePattern", Succeeded: false });
        Assert.Contains(result.Attempts, a => a is { PatternName: "TextPattern", Succeeded: true });
    }

    [Fact]
    public async Task CaptureAsync_reads_a_ComboBox_selection_via_SelectionPattern_when_ValuePattern_and_TextPattern_are_unsupported()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();

        var comboBox = FindById(target, "FormComboBox");
        GetExpandCollapsePattern(comboBox).Expand();
        var firstItem = comboBox.FindFirst(TreeScope.Descendants, Condition.TrueCondition)
            ?? throw new InvalidOperationException("Expected FormComboBox to expose at least one item.");
        GetSelectionItemPattern(firstItem).Select();

        var (resolver, provider) = CreateResolverAndProvider(dispatcher);
        var fingerprint = BuildFingerprint(target, "FormComboBox", "ControlType.ComboBox");
        var handle = await ResolveOrFail(resolver, fingerprint);

        var result = await provider.CaptureAsync(handle, BuildField(fingerprint), CancellationToken.None);

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.Equal("Tablet", result.Value);
        Assert.Contains(result.Attempts, a => a is { PatternName: "ValuePattern", Succeeded: false });
        Assert.Contains(result.Attempts, a => a is { PatternName: "TextPattern", Succeeded: false });
        Assert.Contains(result.Attempts, a => a is { PatternName: "SelectionPattern", Succeeded: true });
    }

    [Fact]
    public async Task CaptureAsync_reads_a_plain_label_through_its_Name_when_it_exposes_no_pattern()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        GetInvokePattern(FindById(target, "AddButton")).Invoke();

        var (resolver, provider) = CreateResolverAndProvider(dispatcher);
        var fingerprint = BuildFingerprint(target, "ItemCountText", "ControlType.Text");
        var handle = await ResolveOrFail(resolver, fingerprint);

        var result = await provider.CaptureAsync(handle, BuildField(fingerprint), CancellationToken.None);

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.Equal("Itens: 1", result.Value);
        Assert.Contains(result.Attempts, a => a is { PatternName: "Name", Succeeded: true });
    }

    [Fact]
    public async Task CaptureAsync_returns_UnsupportedPattern_for_a_control_with_no_compatible_pattern()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();

        var (resolver, provider) = CreateResolverAndProvider(dispatcher);
        var fingerprint = BuildFingerprint(target, "BackButton", "ControlType.Button");
        var handle = await ResolveOrFail(resolver, fingerprint);

        var result = await provider.CaptureAsync(handle, BuildField(fingerprint), CancellationToken.None);

        Assert.Equal(CaptureOutcome.UnsupportedPattern, result.Outcome);
        Assert.Null(result.Value);
        Assert.NotEmpty(result.Attempts);
        Assert.All(result.Attempts, a => Assert.False(a.Succeeded));
    }

    [Fact]
    public async Task CaptureAsync_returns_ElementUnavailable_when_the_element_is_destroyed_before_capture()
    {
        using var dispatcher = new AutomationDispatcher();
        var target = TestTargetLauncher.Launch();

        var (resolver, provider) = CreateResolverAndProvider(dispatcher);
        var fingerprint = BuildFingerprint(target, "DynamicField", "ControlType.Edit");
        var handle = await ResolveOrFail(resolver, fingerprint);

        // Genuinely destroys the element behind this handle - not merely disabling it
        // (the weaker stand-in a previous review flagged) - by killing the whole process
        // that hosts it, between resolution and capture. Any subsequent UI Automation
        // call against the stale AutomationElement this handle wraps must now fail as
        // unavailable.
        target.Dispose();

        var result = await provider.CaptureAsync(handle, BuildField(fingerprint), CancellationToken.None);

        Assert.Equal(CaptureOutcome.ElementUnavailable, result.Outcome);
        Assert.Null(result.Value);
    }

    private static (UiAutomationSelectorResolver Resolver, UiAutomationCaptureProvider Provider) CreateResolverAndProvider(
        AutomationDispatcher dispatcher) =>
        (new UiAutomationSelectorResolver(dispatcher), new UiAutomationCaptureProvider(dispatcher));

    private static async Task<Prescriva.Agent.Application.Selection.ResolvedElementHandle> ResolveOrFail(
        UiAutomationSelectorResolver resolver,
        ElementFingerprint fingerprint)
    {
        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);
        Assert.Equal(Prescriva.Agent.Application.Selection.SelectorResolutionStatus.Found, resolution.Status);
        return resolution.Handle ?? throw new InvalidOperationException("Expected a resolved handle.");
    }

    private static FieldDefinition BuildField(ElementFingerprint fingerprint) =>
        new("field-under-test", "stage-under-test", "Test field", Required: true, fingerprint);

    private static ElementFingerprint BuildFingerprint(TestTargetLauncher target, string automationId, string controlType) =>
        new(
            Process.GetProcessById(target.Window.Current.ProcessId).ProcessName,
            target.Window.Current.Name,
            AutomationId: automationId,
            ControlType: controlType);

    private static AutomationElement FindById(TestTargetLauncher target, string automationId)
    {
        var element = target.Window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        return element ?? throw new InvalidOperationException($"Expected to find an element with AutomationId '{automationId}'.");
    }

    private static ValuePattern GetValuePattern(AutomationElement element) =>
        (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);

    private static InvokePattern GetInvokePattern(AutomationElement element) =>
        (InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern);

    private static ExpandCollapsePattern GetExpandCollapsePattern(AutomationElement element) =>
        (ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern);

    private static SelectionItemPattern GetSelectionItemPattern(AutomationElement element) =>
        (SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern);
}
