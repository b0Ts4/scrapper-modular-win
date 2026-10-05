using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
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

    /// <summary>
    /// Two real, simultaneously running Prescriva.Agent.TestTarget instances have the exact
    /// same process name and the exact same window title - a name+title fingerprint alone
    /// (what <see cref="ElementFingerprint"/> carries) genuinely cannot distinguish them.
    /// This is the RED evidence for the process-ID scoping fix: resolving the identical
    /// fingerprint against the same live desktop state returns a different process's window
    /// depending only on incidental UI Automation enumeration order (which in turn tracks
    /// Z-order), never on anything about the selector itself. Bringing each window to the
    /// foreground in turn - the cheapest reliable way to perturb that enumeration order
    /// without touching production code - demonstrably changes which instance an unscoped
    /// resolver returns for the exact same fingerprint, proving the resolution is
    /// nondeterministic rather than "usually picks the right one by luck".
    /// </summary>
    [Fact]
    public async Task ResolveAsync_without_a_process_id_is_not_reliably_scoped_to_one_of_two_identical_instances()
    {
        using var first = TestTargetLauncher.Launch();
        using var second = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();

        SetMedicationValue(first, "FIRST-INSTANCE-VALUE");
        SetMedicationValue(second, "SECOND-INSTANCE-VALUE");

        // Unscoped: exactly what production code did before this fix - no process ID at all.
        var unscopedResolver = new UiAutomationSelectorResolver(dispatcher);
        var captureProvider = new UiAutomationCaptureProvider(dispatcher);
        var fingerprint = BuildFingerprint(first, "MedicationTextBox", "ControlType.Edit");
        var field = new FieldDefinition(
            "medication", "stage-under-test", "Medication", Required: true, fingerprint);

        first.Window.SetFocus();
        await Task.Delay(250);
        var capturedWhenFirstFocused = await ResolveAndCaptureAsync(unscopedResolver, captureProvider, fingerprint, field);

        second.Window.SetFocus();
        await Task.Delay(250);
        var capturedWhenSecondFocused = await ResolveAndCaptureAsync(unscopedResolver, captureProvider, fingerprint, field);

        // The exact same fingerprint, against the exact same two live windows, resolved to
        // a different process's TextBox value purely because of which window had focus -
        // there is no notion of "the right instance" in the unscoped path at all. (If this
        // assertion ever fails because both happen to resolve to the same instance on some
        // machine, that does not mean the gap is closed - it means enumeration order did not
        // change between the two focus changes on that run. The scoped test below is what
        // actually proves the fix; this test only documents why the unscoped path cannot be
        // trusted.)
        Assert.NotEqual(capturedWhenFirstFocused, capturedWhenSecondFocused);
    }

    /// <summary>
    /// GREEN evidence for the fix: with a process ID supplied, resolving the exact same
    /// name+title+AutomationId fingerprint against two simultaneously running, identically
    /// named/titled instances always returns the element belonging to the instance whose PID
    /// was requested - verified by actually capturing each resolved element's live value and
    /// confirming it matches what was written into that specific process's own TextBox, not
    /// the other process's.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_scoped_to_a_process_id_always_resolves_that_specific_instance()
    {
        using var first = TestTargetLauncher.Launch();
        using var second = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();

        var firstProcessId = first.Window.Current.ProcessId;
        var secondProcessId = second.Window.Current.ProcessId;

        SetMedicationValue(first, "FIRST-INSTANCE-VALUE");
        SetMedicationValue(second, "SECOND-INSTANCE-VALUE");

        var firstResolver = new UiAutomationSelectorResolver(dispatcher, processId: firstProcessId);
        var secondResolver = new UiAutomationSelectorResolver(dispatcher, processId: secondProcessId);
        var captureProvider = new UiAutomationCaptureProvider(dispatcher);

        var fingerprint = BuildFingerprint(first, "MedicationTextBox", "ControlType.Edit");
        var field = new FieldDefinition(
            "medication", "stage-under-test", "Medication", Required: true, fingerprint);

        // Flip focus between each resolution, same as the RED test above, to prove the
        // process-ID scoping - not incidental enumeration order - is what determines the
        // outcome.
        second.Window.SetFocus();
        await Task.Delay(250);
        var firstResolution = await firstResolver.ResolveAsync(fingerprint, CancellationToken.None);
        Assert.Equal(SelectorResolutionStatus.Found, firstResolution.Status);
        var firstCapture = await captureProvider.CaptureAsync(firstResolution.Handle!, field, CancellationToken.None);
        Assert.Equal("FIRST-INSTANCE-VALUE", firstCapture.Value);

        first.Window.SetFocus();
        await Task.Delay(250);
        var secondResolution = await secondResolver.ResolveAsync(fingerprint, CancellationToken.None);
        Assert.Equal(SelectorResolutionStatus.Found, secondResolution.Status);
        var secondCapture = await captureProvider.CaptureAsync(secondResolution.Handle!, field, CancellationToken.None);
        Assert.Equal("SECOND-INSTANCE-VALUE", secondCapture.Value);
    }

    private static async Task<string?> ResolveAndCaptureAsync(
        UiAutomationSelectorResolver resolver,
        UiAutomationCaptureProvider captureProvider,
        ElementFingerprint fingerprint,
        FieldDefinition field)
    {
        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);
        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);
        var result = await captureProvider.CaptureAsync(resolution.Handle!, field, CancellationToken.None);
        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        return result.Value;
    }

    private static void SetMedicationValue(TestTargetLauncher target, string value)
    {
        var element = target.Window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "MedicationTextBox"))
            ?? throw new InvalidOperationException("Expected to find MedicationTextBox.");
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    }

    private static ElementFingerprint BuildFingerprint(TestTargetLauncher target, string automationId, string controlType) =>
        new(
            Process.GetProcessById(target.Window.Current.ProcessId).ProcessName,
            target.Window.Current.Name,
            AutomationId: automationId,
            ControlType: controlType);
}
