using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// Exercises UiAutomationSelectorResolver against a real, launched
/// Prescriva.Agent.TestTarget process, so these prove behavior against live UI
/// Automation rather than a mock or fake.
/// </summary>
public sealed class SelectorResolutionTests
{
    [Fact]
    public async Task ResolveAsync_finds_the_exact_element_by_automation_id()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var resolver = new UiAutomationSelectorResolver(dispatcher);

        var fingerprint = BuildFingerprint(target, "MedicationTextBox", "ControlType.Edit");

        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);

        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);
        Assert.NotNull(resolution.Handle);
    }

    [Fact]
    public async Task ResolveAsync_finds_the_element_after_the_layout_moves()
    {
        using var target = TestTargetLauncher.Launch(layoutVariant: "alternate");
        using var dispatcher = new AutomationDispatcher();
        var resolver = new UiAutomationSelectorResolver(dispatcher);

        var fingerprint = BuildFingerprint(target, "MedicationTextBox", "ControlType.Edit");

        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);

        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);
        Assert.NotNull(resolution.Handle);
    }

    [Fact]
    public async Task ResolveAsync_returns_Ambiguous_for_deliberately_duplicated_controls()
    {
        using var target = TestTargetLauncher.Launch(layoutVariant: "duplicate-controls");
        using var dispatcher = new AutomationDispatcher();
        var resolver = new UiAutomationSelectorResolver(dispatcher);

        // Deliberately omits Name so both the real MedicationTextBox and the duplicate
        // control the TestTarget adds for this variant score identically.
        var fingerprint = BuildFingerprint(target, "MedicationTextBox", "ControlType.Edit");

        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);

        Assert.Equal(SelectorResolutionStatus.Ambiguous, resolution.Status);
        Assert.Null(resolution.Handle);
    }

    [Fact]
    public async Task ResolveAsync_returns_WindowMissing_once_the_target_process_has_exited()
    {
        string processIdentity;
        string windowRule;
        using (var target = TestTargetLauncher.Launch())
        {
            processIdentity = Process.GetProcessById(target.Window.Current.ProcessId).ProcessName;
            windowRule = target.Window.Current.Name;
        }

        using var dispatcher = new AutomationDispatcher();
        var resolver = new UiAutomationSelectorResolver(dispatcher);

        var fingerprint = new ElementFingerprint(
            processIdentity,
            windowRule,
            AutomationId: "MedicationTextBox",
            ControlType: "ControlType.Edit");

        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);

        Assert.Equal(SelectorResolutionStatus.WindowMissing, resolution.Status);
        Assert.Null(resolution.Handle);
    }

    [Fact]
    public async Task ResolveAsync_throws_OperationCanceledException_when_its_own_token_is_cancelled()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var resolver = new UiAutomationSelectorResolver(dispatcher);

        var fingerprint = BuildFingerprint(target, "MedicationTextBox", "ControlType.Edit");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(fingerprint, cts.Token));
    }

    [Fact]
    public void ResolvedElementHandle_never_exposes_a_native_UI_Automation_reference()
    {
        foreach (var property in typeof(ResolvedElementHandle).GetProperties())
        {
            Assert.False(
                typeof(AutomationElement).IsAssignableFrom(property.PropertyType),
                $"ResolvedElementHandle.{property.Name} must not expose a native AutomationElement.");
        }

        Assert.True(typeof(ResolvedElementHandle).IsAbstract, "ResolvedElementHandle must be abstract.");

        var publicConstructors = typeof(ResolvedElementHandle)
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.Empty(publicConstructors);
    }

    private static ElementFingerprint BuildFingerprint(TestTargetLauncher target, string automationId, string controlType) =>
        new(
            Process.GetProcessById(target.Window.Current.ProcessId).ProcessName,
            target.Window.Current.Name,
            AutomationId: automationId,
            ControlType: controlType);
}
