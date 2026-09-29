using System.Collections.Immutable;
using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// Exercises UiAutomationTriggerProvider against a real, launched
/// Prescriva.Agent.TestTarget process, so these prove behavior against live UI
/// Automation invoke events rather than a mock or fake.
/// </summary>
public sealed class TriggerProviderTests
{
    [Theory]
    [InlineData("AddButton", "add-item")]
    [InlineData("FinishButton", "finish-session")]
    public async Task WatchAsync_yields_a_signal_when_the_real_button_is_invoked(string automationId, string triggerId)
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var sessionId = Guid.NewGuid();
        using var provider = new UiAutomationTriggerProvider(dispatcher, sessionId);

        var trigger = BuildTrigger(target, automationId, triggerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var enumerator = provider.WatchAsync(trigger, cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var moveNextTask = enumerator.MoveNextAsync().AsTask();

            // The subscription is registered asynchronously on the dispatcher thread;
            // give it a moment before invoking, and retry the invoke a couple of times in
            // case the first one lands before the handler is attached.
            TriggerSignal? signal = null;
            for (var attempt = 0; attempt < 5 && signal is null; attempt++)
            {
                await Task.Delay(200);
                InvokeButton(target, automationId);

                var completed = await Task.WhenAny(moveNextTask, Task.Delay(500));
                if (completed == moveNextTask)
                {
                    Assert.True(await moveNextTask);
                    signal = enumerator.Current;
                }
            }

            Assert.NotNull(signal);
            Assert.Equal(sessionId, signal!.SessionId);
            Assert.Equal(triggerId, signal.TriggerId);
        }
        finally
        {
            cts.Cancel();
            await SafeDisposeAsync(enumerator);
        }
    }

    [Fact]
    public async Task WatchAsync_throws_a_typed_failure_when_the_watched_element_disappears_mid_watch()
    {
        var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        using var provider = new UiAutomationTriggerProvider(
            dispatcher,
            Guid.NewGuid(),
            livenessPollInterval: TimeSpan.FromMilliseconds(50));

        var trigger = BuildTrigger(target, "AddButton", "add-item");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var enumerator = provider.WatchAsync(trigger, cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var moveNextTask = enumerator.MoveNextAsync().AsTask();

            // Let the subscription and the liveness poll both get established, then kill
            // the whole process out from under the watch - the window, and every element
            // in it, stop existing.
            await Task.Delay(300);
            target.Dispose();

            var failure = await Assert.ThrowsAsync<ElementInspectionFailure>(() => moveNextTask);
            Assert.True(
                failure.Kind is ElementInspectionFailureKind.ElementUnavailable or ElementInspectionFailureKind.WindowMissing,
                $"Expected ElementUnavailable or WindowMissing, got {failure.Kind}.");
        }
        finally
        {
            cts.Cancel();
            await SafeDisposeAsync(enumerator);
        }
    }

    [Fact]
    public async Task WatchAsync_stops_cleanly_and_promptly_when_cancelled()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        using var provider = new UiAutomationTriggerProvider(dispatcher, Guid.NewGuid());

        var trigger = BuildTrigger(target, "AddButton", "add-item");

        using var cts = new CancellationTokenSource();
        var enumerator = provider.WatchAsync(trigger, cts.Token).GetAsyncEnumerator(cts.Token);

        var moveNextTask = enumerator.MoveNextAsync().AsTask();

        // Let the subscription actually get established before cancelling, so this
        // exercises a real unsubscribe rather than a cancellation that lands before the
        // handler was ever attached.
        await Task.Delay(300);
        cts.Cancel();

        // The provider's own finally block unsubscribes the real UIA handler (via the
        // dispatcher) before this task completes. Bounding the wait proves that
        // teardown does not hang - if RemoveAutomationEventHandler were left dangling,
        // or the dispatcher thread were stuck, this would time out instead of observing
        // the expected OperationCanceledException promptly.
        var completed = await Task.WhenAny(moveNextTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(moveNextTask, completed);
        await Assert.ThrowsAsync<OperationCanceledException>(() => moveNextTask);

        await SafeDisposeAsync(enumerator);
    }

    private static void InvokeButton(TestTargetLauncher target, string automationId)
    {
        var button = target.Window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        Assert.NotNull(button);

        var pattern = (InvokePattern)button!.GetCurrentPattern(InvokePattern.Pattern);
        pattern.Invoke();
    }

    private static TriggerDefinition BuildTrigger(TestTargetLauncher target, string automationId, string triggerId) =>
        new(
            triggerId,
            "stage-1",
            new ElementFingerprint(
                Process.GetProcessById(target.Window.Current.ProcessId).ProcessName,
                target.Window.Current.Name,
                AutomationId: automationId,
                ControlType: "ControlType.Button"),
            "Invoke",
            ImmutableArray<TriggerActionDefinition>.Empty);

    private static async Task SafeDisposeAsync(IAsyncEnumerator<TriggerSignal> enumerator)
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ElementInspectionFailure)
        {
        }
    }
}
