using System.Runtime.CompilerServices;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;
using Prescriva.Agent.Windows.Processes;

namespace Prescriva.Agent.Windows.IntegrationTests.RealApps;

/// <summary>
/// The real Windows Calculator - classic (win32calc, present on Windows Server and the CI runner)
/// or the Store app (CalculatorApp, framed by ApplicationFrameHost on Windows 10/11): listed by
/// its content process, its display found by AutomationId and read after typing 3 9 2, its "="
/// button watched as a trigger, and its running instance discovered. Skipped, with the reason,
/// where no calculator exists.
/// </summary>
public sealed class RealCalculatorTests
{
    [SkippableFact]
    public async Task The_calculator_is_listed_by_the_process_that_owns_its_content()
    {
        using var calculator = await StartCalculatorAsync();

        Assert.NotEqual(Win32OpenWindowSource.FrameHostProcessName, calculator.Window.ProcessName, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(calculator.Window.ProcessName, CalculatorIds.ProcessNames, StringComparer.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(calculator.Window.WindowTitle));
    }

    [SkippableFact]
    public async Task The_display_is_found_by_AutomationId_and_read_after_typing_392()
    {
        using var calculator = await StartCalculatorAsync();
        var ids = CalculatorIds.For(calculator.Window);
        foreach (var digit in new[] { 3, 9, 2 })
        {
            calculator.Press(ids.Digits[digit]);
        }

        using var dispatcher = new AutomationDispatcher();
        using var resolver = new UiAutomationSelectorResolver(dispatcher, processId: calculator.Window.ProcessId);
        var display = Selector(calculator, ids.Display);
        var resolution = await resolver.ResolveAsync(display, CancellationToken.None);
        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);

        using var capture = new UiAutomationCaptureProvider(dispatcher);
        var result = await capture.CaptureAsync(resolution.Handle!, new FieldDefinition("display", "calc", "Visor", true, display), CancellationToken.None);

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.Contains("392", result.Value, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Pressing_equals_is_detected_as_a_trigger()
    {
        using var calculator = await StartCalculatorAsync();
        var ids = CalculatorIds.For(calculator.Window);

        using var dispatcher = new AutomationDispatcher();
        using var provider = new UiAutomationTriggerProvider(dispatcher, Guid.NewGuid(), processId: calculator.Window.ProcessId);
        var trigger = new TriggerDefinition("equals", "calc", Selector(calculator, ids.EqualsButton), "Invoke", [new EmitEventAction("calculated")]);
        using var watch = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var established = new TaskCompletionSource();
        provider.WatchEstablished += (_, _) => established.TrySetResult();

        var signal = FirstSignalAsync(provider.WatchAsync(trigger, watch.Token));
        await established.Task.WaitAsync(TimeSpan.FromSeconds(15));
        calculator.Press(ids.EqualsButton);

        Assert.Equal("equals", (await signal).TriggerId);
    }

    [SkippableFact]
    public async Task The_running_calculator_is_discovered_as_an_application_instance()
    {
        using var calculator = await StartCalculatorAsync();
        var source = new WindowsApplicationInstanceSource(pollInterval: TimeSpan.FromMilliseconds(200));
        using var watch = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await foreach (var change in source.WatchAsync(new ApplicationDefinition(calculator.Window.ProcessName, calculator.Window.WindowTitle), watch.Token))
        {
            if (change.Instance.ProcessId == calculator.Window.ProcessId)
            {
                Assert.Equal(Prescriva.Agent.Application.Runtime.ApplicationInstanceChangeKind.Started, change.Kind);
                return;
            }
        }

        Assert.Fail("The running calculator was not discovered.");
    }

    [SkippableFact]
    public async Task The_Store_calculator_is_framed_by_ApplicationFrameHost_yet_identified_by_CalculatorApp()
    {
        using var calculator = await StartCalculatorAsync();
        Skip.If(CalculatorIds.For(calculator.Window) != CalculatorIds.Store,
            $"The Store Calculator (CalculatorApp) is not installed here; '{calculator.Window.ProcessName}' started instead (the CI runner, Windows Server 2025, only has the classic win32calc).");

        Assert.NotEqual(calculator.Window.ProcessId, calculator.Element.Current.ProcessId); // frame: ApplicationFrameHost
        using var dispatcher = new AutomationDispatcher();
        using var resolver = new UiAutomationSelectorResolver(dispatcher, processId: calculator.Window.ProcessId);
        Assert.Equal(SelectorResolutionStatus.Found, (await resolver.ResolveAsync(Selector(calculator, "CalculatorResults"), CancellationToken.None)).Status);
    }

    private static async Task<RealApp> StartCalculatorAsync()
    {
        var calculator = await RealApp.StartAsync("calc.exe", CalculatorIds.ProcessNames);
        Skip.If(calculator is null, "No Windows Calculator on this machine (calc.exe did not open win32calc or CalculatorApp).");
        return calculator!;
    }

    private static ElementFingerprint Selector(RealApp app, string automationId) =>
        new(app.Window.ProcessName, app.Window.WindowTitle, AutomationId: automationId);

    private static async Task<Prescriva.Agent.Application.Triggers.TriggerSignal> FirstSignalAsync(
        IAsyncEnumerable<Prescriva.Agent.Application.Triggers.TriggerSignal> signals,
        [CallerMemberName] string? caller = null)
    {
        await foreach (var signal in signals)
        {
            return signal;
        }

        throw new InvalidOperationException($"{caller}: the trigger watch ended without a signal.");
    }
}
