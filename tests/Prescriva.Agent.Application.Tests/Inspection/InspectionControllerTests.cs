using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Tests.Inspection;

/// <summary>
/// Exercises InspectionController against a fake IElementInspector - no real UI
/// Automation involved. These prove the controller's orchestration (throttling stale
/// pointer updates, stopping, confirming what was actually displayed, excluding the
/// Agent's own process) independent of any live UI.
/// </summary>
public sealed class InspectionControllerTests
{
    private static ElementSnapshot Snapshot(string automationId, int processId = 999, string processName = "SomeApp.exe") =>
        new(
            automationId,
            $"{automationId} name",
            "ControlType.Edit",
            "SomeClass",
            new BoundingRectangle(10, 20, 30, 40),
            processId,
            processName,
            "Some Window");

    [Fact]
    public async Task StartAsync_produces_a_state_with_no_highlight()
    {
        var inspector = new FakeElementInspector();
        var controller = new InspectionController(inspector);

        await controller.StartAsync();

        Assert.Null(controller.CurrentState.Snapshot);
        Assert.Null(controller.CurrentState.Bounds);
        Assert.Null(controller.CurrentState.Fingerprint);
    }

    [Fact]
    public async Task ObservePointerAsync_found_element_updates_snapshot_bounds_and_fingerprint()
    {
        var inspector = new FakeElementInspector();
        var snapshot = Snapshot("MedicationTextBox");
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(snapshot)));

        var controller = new InspectionController(inspector);
        await controller.StartAsync();

        await controller.ObservePointerAsync(new ScreenPoint(1, 2));

        var state = controller.CurrentState;
        Assert.Equal(snapshot, state.Snapshot);
        Assert.Equal(snapshot.BoundingRectangle, state.Bounds);
        Assert.NotNull(state.Fingerprint);
        Assert.Equal(snapshot.AutomationId, state.Fingerprint!.AutomationId);
        Assert.Equal(snapshot.ProcessName, state.Fingerprint.ProcessIdentity);
        Assert.Equal(snapshot.WindowTitle, state.Fingerprint.WindowRule);
    }

    [Fact]
    public async Task ObservePointerAsync_a_not_found_result_clears_the_previous_snapshot()
    {
        var inspector = new FakeElementInspector();
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(Snapshot("FirstElement"))));
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.WindowMissing("gone")));

        var controller = new InspectionController(inspector);
        await controller.StartAsync();
        await controller.ObservePointerAsync(new ScreenPoint(1, 2));
        await controller.ObservePointerAsync(new ScreenPoint(3, 4));

        var state = controller.CurrentState;
        Assert.Null(state.Snapshot);
        Assert.Null(state.Bounds);
        Assert.Null(state.Fingerprint);
        Assert.NotEmpty(state.Warnings);
    }

    [Fact]
    public async Task Rapid_pointer_updates_cancel_the_stale_in_flight_inspection()
    {
        var inspector = new FakeElementInspector();
        var firstCallCts = new TaskCompletionSource<CancellationToken>();
        var firstCallResult = new TaskCompletionSource<InspectionResult>();
        inspector.Enqueue(ct =>
        {
            firstCallCts.TrySetResult(ct);
            return firstCallResult.Task;
        });

        var secondSnapshot = Snapshot("SecondElement");
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(secondSnapshot)));

        var controller = new InspectionController(inspector);
        await controller.StartAsync();

        var firstObservation = controller.ObservePointerAsync(new ScreenPoint(1, 1));

        // Wait until the first call is actually in flight before firing the second -
        // otherwise this proves nothing about cancelling in-flight work.
        var firstToken = await firstCallCts.Task;

        var secondObservation = controller.ObservePointerAsync(new ScreenPoint(2, 2));
        await secondObservation;

        Assert.True(firstToken.IsCancellationRequested);

        // Let the stale first call unblock (as if the underlying inspector eventually
        // honored cancellation) and make sure it never clobbers the newer state.
        firstCallResult.TrySetCanceled(firstToken);
        await firstObservation;

        Assert.Equal(secondSnapshot, controller.CurrentState.Snapshot);
    }

    /// <summary>
    /// Covers the same invariant as <see cref="Rapid_pointer_updates_cancel_the_stale_in_flight_inspection"/>
    /// but for the case where the stale call's underlying inspection does not honor
    /// cancellation and instead completes successfully (a real Found result), arriving
    /// well after a newer call has already published its own state. A prior version of
    /// InspectionController checked "am I still the current call?" and wrote the new
    /// state as two separate steps (a check, then a later-acquired lock), which left a
    /// narrow window in which a superseding call's CancellationTokenSource swap could
    /// land between them - letting a stale result win the race and briefly clobber
    /// CurrentState with old data. This test cannot force that exact nanosecond-scale
    /// interleaving deterministically (nothing observable from the public API pins the
    /// two steps apart at that granularity), but it does pin down the guarantee the fix
    /// provides: regardless of how late a stale result arrives, it must never overwrite
    /// state a newer call has already published.
    /// </summary>
    [Fact]
    public async Task A_stale_result_that_completes_successfully_after_a_newer_call_already_won_is_dropped()
    {
        var inspector = new FakeElementInspector();
        var firstCallResult = new TaskCompletionSource<InspectionResult>();
        inspector.Enqueue(_ => firstCallResult.Task);

        var secondSnapshot = Snapshot("SecondElement");
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(secondSnapshot)));

        var controller = new InspectionController(inspector);
        await controller.StartAsync();

        var firstObservation = controller.ObservePointerAsync(new ScreenPoint(1, 1));
        await controller.ObservePointerAsync(new ScreenPoint(2, 2));

        // The second call has already run to completion and published its state. Now let
        // the stale first call resolve successfully (not via cancellation) with a
        // different element entirely.
        Assert.Equal(secondSnapshot, controller.CurrentState.Snapshot);
        var staleSnapshot = Snapshot("StaleFirstElement");
        firstCallResult.TrySetResult(InspectionResult.Found(staleSnapshot));
        await firstObservation;

        Assert.Equal(secondSnapshot, controller.CurrentState.Snapshot);
    }

    [Fact]
    public async Task StopAsync_removes_the_highlight()
    {
        var inspector = new FakeElementInspector();
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(Snapshot("SomeElement"))));

        var controller = new InspectionController(inspector);
        await controller.StartAsync();
        await controller.ObservePointerAsync(new ScreenPoint(1, 2));
        Assert.NotNull(controller.CurrentState.Snapshot);

        await controller.StopAsync();

        var state = controller.CurrentState;
        Assert.Null(state.Snapshot);
        Assert.Null(state.Bounds);
        Assert.Null(state.Fingerprint);
    }

    [Fact]
    public async Task ConfirmAsync_captures_the_fingerprint_that_was_actually_displayed()
    {
        var inspector = new FakeElementInspector();
        var displayedSnapshot = Snapshot("DisplayedElement");
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(displayedSnapshot)));

        var controller = new InspectionController(inspector);
        await controller.StartAsync();
        await controller.ObservePointerAsync(new ScreenPoint(1, 2));

        var callsBeforeConfirm = inspector.CallCount;
        var confirmed = await controller.ConfirmAsync();

        // Confirming must not re-resolve against the live inspector - only the snapshot
        // already displayed to the user is captured.
        Assert.Equal(callsBeforeConfirm, inspector.CallCount);
        Assert.Equal(displayedSnapshot.AutomationId, confirmed.Fingerprint?.AutomationId);
        Assert.Equal(displayedSnapshot, confirmed.Snapshot);
    }

    [Fact]
    public async Task ConfirmAsync_throws_when_nothing_is_currently_displayed()
    {
        var inspector = new FakeElementInspector();
        var controller = new InspectionController(inspector);
        await controller.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ConfirmAsync());
    }

    [Fact]
    public async Task ObservePointerAsync_excludes_elements_owned_by_the_agents_own_process()
    {
        var inspector = new FakeElementInspector();
        var ownSnapshot = Snapshot("OverlayHighlightBorder", processId: Environment.ProcessId, processName: "Prescriva.Agent.Desktop");
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(ownSnapshot)));

        var controller = new InspectionController(inspector);
        await controller.StartAsync();
        await controller.ObservePointerAsync(new ScreenPoint(1, 2));

        var state = controller.CurrentState;
        Assert.Null(state.Snapshot);
        Assert.Null(state.Fingerprint);
        Assert.Contains(state.Warnings, w => w.Contains("own process", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Additional_excluded_process_ids_are_also_ignored()
    {
        var inspector = new FakeElementInspector();
        var excludedSnapshot = Snapshot("SomeAuxiliaryAgentWindow", processId: 4242, processName: "Prescriva.Agent.Helper");
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(excludedSnapshot)));

        var controller = new InspectionController(inspector, additionalExcludedProcessIds: new[] { 4242 });
        await controller.StartAsync();
        await controller.ObservePointerAsync(new ScreenPoint(1, 2));

        Assert.Null(controller.CurrentState.Snapshot);
    }

    [Fact]
    public async Task ConfirmAsync_still_confirms_the_last_real_element_after_the_pointer_crosses_onto_the_agents_own_window()
    {
        // Reproduces the "Confirm Selection cannot work with a real mouse" bug: the user
        // hovers a real target (seen and highlighted), then moves the pointer across the
        // Agent's own window on the way to click "Confirm". That second observation
        // resolves to an element owned by the Agent's own (excluded) process - it must
        // not wipe out the real snapshot the user is trying to confirm.
        var inspector = new FakeElementInspector();
        var realSnapshot = Snapshot("MedicationTextBox");
        var ownSnapshot = Snapshot("ConfirmSelectionButton", processId: Environment.ProcessId, processName: "Prescriva.Agent.Desktop");
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(realSnapshot)));
        inspector.Enqueue(_ => Task.FromResult(InspectionResult.Found(ownSnapshot)));

        var controller = new InspectionController(inspector);
        await controller.StartAsync();

        await controller.ObservePointerAsync(new ScreenPoint(1, 2));
        Assert.Equal("MedicationTextBox", controller.CurrentState.Snapshot?.AutomationId);

        await controller.ObservePointerAsync(new ScreenPoint(3, 4));
        Assert.Contains(controller.CurrentState.Warnings, w => w.Contains("own process", StringComparison.OrdinalIgnoreCase));

        var confirmed = await controller.ConfirmAsync();

        Assert.Equal("MedicationTextBox", confirmed.Snapshot?.AutomationId);
        Assert.Equal("MedicationTextBox", confirmed.Fingerprint?.AutomationId);
    }

    private sealed class FakeElementInspector : IElementInspector
    {
        private readonly Queue<Func<CancellationToken, Task<InspectionResult>>> _responses = new();

        public int CallCount { get; private set; }

        public void Enqueue(Func<CancellationToken, Task<InspectionResult>> factory) => _responses.Enqueue(factory);

        public Task<InspectionResult> FromPointAsync(ScreenPoint point, TimeSpan timeout, CancellationToken cancellationToken)
        {
            CallCount++;
            var factory = _responses.Count > 0
                ? _responses.Dequeue()
                : _ => Task.FromResult(InspectionResult.TimedOut());

            return factory(cancellationToken);
        }

        public Task<IReadOnlyList<ElementSnapshot>> FindCandidatesAsync(
            ApplicationDefinition application,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("InspectionController never calls FindCandidatesAsync.");
    }
}
