using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Diagnostics;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Domain.Sessions;

namespace Prescriva.Agent.Application.Tests.Runtime;

/// <summary>
/// Exercises AgentRuntime's own bookkeeping (StartInstance/StopInstanceAsync/the `_active`
/// dictionary) directly - the code most responsible for this plan's "two instances maintain
/// independent sessions" review focus item - against a fake <see cref="IApplicationInstanceSource"/>
/// and fake trigger/resolver/capture providers. No real <see cref="System.Diagnostics.Process"/>
/// or UI Automation involved anywhere in this file, so it runs instantly and deterministically
/// in any environment; <c>MultipleInstanceTests</c> (Windows.IntegrationTests) proves the same
/// property against real launched processes and stays as the slower, real-process companion to
/// this fast unit-level proof.
/// </summary>
public sealed class AgentRuntimeTests
{
    private const string StageId = "stage-1";
    private const string MarkTriggerId = "mark";

    [Fact]
    public async Task ActivateAsync_starts_one_independent_coordinator_per_discovered_instance()
    {
        var source = new FakeApplicationInstanceSource();
        var outbox = new FakeEventOutbox();
        var log = new FakeTechnicalLog();
        var triggerProvidersByInstanceId = new ConcurrentDictionary<Guid, FakeTriggerProvider>();

        // Deterministic, predictable session IDs assigned in the order instances are started,
        // so the test can tell which outbox event belongs to which instance.
        var sessionIds = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid()]);

        var runtime = new AgentRuntime(
            source,
            triggerProviderFactory: (instance, sessionId) =>
            {
                var provider = new FakeTriggerProvider(sessionId);
                triggerProvidersByInstanceId[instance.InstanceId] = provider;
                return provider;
            },
            selectorResolverFactory: _ => new UnusedSelectorResolver(),
            captureProviderFactory: _ => new UnusedCaptureProvider(),
            outbox,
            log,
            sessionIdFactory: () => sessionIds.Dequeue());

        using var activationCts = new CancellationTokenSource();
        var configuration = BuildConfiguration();
        var approval = new ConfigurationApproval(configuration.Id, ConfigurationFingerprint.Compute(configuration), DateTimeOffset.UtcNow);
        var activateTask = runtime.ActivateAsync(configuration, approval, activationCts.Token);

        try
        {
            var instanceA = new ApplicationInstance(Guid.NewGuid(), ProcessId: 1001);
            var instanceB = new ApplicationInstance(Guid.NewGuid(), ProcessId: 2002);

            source.Enqueue(new ApplicationInstanceChange(instanceA, ApplicationInstanceChangeKind.Started));
            source.Enqueue(new ApplicationInstanceChange(instanceB, ApplicationInstanceChangeKind.Started));

            await WaitUntilAsync(
                () => triggerProvidersByInstanceId.ContainsKey(instanceA.InstanceId) && triggerProvidersByInstanceId.ContainsKey(instanceB.InstanceId),
                TimeSpan.FromSeconds(5));

            Assert.Equal(2, runtime.ActiveSessions.Count);
            var coordinatorA = runtime.ActiveSessions[instanceA.InstanceId];
            var coordinatorB = runtime.ActiveSessions[instanceB.InstanceId];
            Assert.NotEqual(coordinatorA.SessionId, coordinatorB.SessionId);

            // Fire only instance A's trigger - only its session may advance.
            triggerProvidersByInstanceId[instanceA.InstanceId].FireOnce(MarkTriggerId);
            await WaitUntilAsync(() => outbox.Appended.Count >= 1, TimeSpan.FromSeconds(5));

            Assert.Single(outbox.Appended);
            Assert.Equal(coordinatorA.SessionId, outbox.Appended[0].SessionId);
            Assert.Equal(1, outbox.Appended[0].Sequence);
            Assert.Equal(1, coordinatorA.CurrentSession.LastSequence);
            Assert.Equal(0, coordinatorB.CurrentSession.LastSequence); // untouched by A's occurrence

            // Fire instance B's trigger - it gets its own independent sequence, starting at 1
            // again, not continuing instance A's sequence.
            triggerProvidersByInstanceId[instanceB.InstanceId].FireOnce(MarkTriggerId);
            await WaitUntilAsync(() => outbox.Appended.Count >= 2, TimeSpan.FromSeconds(5));

            Assert.Equal(2, outbox.Appended.Count);
            var instanceBEvent = outbox.Appended.Single(e => e.SessionId == coordinatorB.SessionId);
            Assert.Equal(1, instanceBEvent.Sequence);
            Assert.Equal(1, coordinatorA.CurrentSession.LastSequence); // still just the one from before
            Assert.Equal(1, coordinatorB.CurrentSession.LastSequence);

            // Stopping instance A must end only its own session (remove it from
            // ActiveSessions, close its CaptureSession) and leave instance B's session and
            // ActiveSessions entry completely untouched.
            source.Enqueue(new ApplicationInstanceChange(instanceA, ApplicationInstanceChangeKind.Stopped));
            await WaitUntilAsync(() => !runtime.ActiveSessions.ContainsKey(instanceA.InstanceId), TimeSpan.FromSeconds(5));

            // ActiveSessions.Remove happens synchronously before CloseAsync's own (async)
            // state mutation completes - wait for the actual state transition too, rather
            // than assuming it has already landed the instant the dictionary entry is gone.
            await WaitUntilAsync(() => coordinatorA.CurrentSession.State == SessionState.Cancelled, TimeSpan.FromSeconds(5));
            Assert.True(runtime.ActiveSessions.ContainsKey(instanceB.InstanceId));
            Assert.Equal(SessionState.Active, coordinatorB.CurrentSession.State);
            Assert.Equal(1, coordinatorB.CurrentSession.LastSequence);
        }
        finally
        {
            activationCts.Cancel();
            try
            {
                await activateTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Stopping_by_cancelling_the_activation_token_is_a_normal_end_even_if_the_source_throws_on_cancellation()
    {
        // The fake source throws OperationCanceledException on cancellation (as a real source
        // does when cancellation lands outside its own polling wait): stopping monitoring is
        // still a clean end, with every session closed.
        var source = new FakeApplicationInstanceSource();
        var runtime = new AgentRuntime(
            source,
            triggerProviderFactory: (_, sessionId) => new FakeTriggerProvider(sessionId),
            selectorResolverFactory: _ => new UnusedSelectorResolver(),
            captureProviderFactory: _ => new UnusedCaptureProvider(),
            new FakeEventOutbox(),
            new FakeTechnicalLog());
        var configuration = BuildConfiguration();
        var approval = new ConfigurationApproval(configuration.Id, ConfigurationFingerprint.Compute(configuration), DateTimeOffset.UtcNow);
        using var activationCts = new CancellationTokenSource();

        var activation = runtime.ActivateAsync(configuration, approval, activationCts.Token);
        source.Enqueue(new ApplicationInstanceChange(new ApplicationInstance(Guid.NewGuid(), ProcessId: 1001), ApplicationInstanceChangeKind.Started));
        await WaitUntilAsync(() => runtime.ActiveSessions.Count == 1, TimeSpan.FromSeconds(5));
        activationCts.Cancel();

        Assert.Equal(ActivationStatus.Activated, (await activation.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Empty(runtime.ActiveSessions);
    }

    [Fact]
    public async Task ActivateAsync_returns_NotTested_and_never_watches_instances_when_approval_is_missing()
    {
        var source = new NeverWatchedApplicationInstanceSource();
        var runtime = new AgentRuntime(
            source,
            triggerProviderFactory: (_, sessionId) => new FakeTriggerProvider(sessionId),
            selectorResolverFactory: _ => new UnusedSelectorResolver(),
            captureProviderFactory: _ => new UnusedCaptureProvider(),
            new FakeEventOutbox(),
            new FakeTechnicalLog());

        var result = await runtime.ActivateAsync(BuildConfiguration(), approval: null, CancellationToken.None);

        Assert.Equal(ActivationStatus.NotTested, result.Status);
        Assert.False(source.WasWatched);
        Assert.Empty(runtime.ActiveSessions);
    }

    [Fact]
    public async Task ActivateAsync_returns_NotTested_when_the_approval_no_longer_matches_the_configurations_current_fingerprint()
    {
        var configuration = BuildConfiguration();

        // An approval genuinely granted for this same configuration ID, but for different
        // content (a stale approval left over from before an edit) - must not be confused
        // with "no approval at all", but must still refuse activation.
        var staleApproval = new ConfigurationApproval(
            configuration.Id,
            Fingerprint: "not-the-real-fingerprint",
            DateTimeOffset.UtcNow);

        var source = new NeverWatchedApplicationInstanceSource();
        var runtime = new AgentRuntime(
            source,
            triggerProviderFactory: (_, sessionId) => new FakeTriggerProvider(sessionId),
            selectorResolverFactory: _ => new UnusedSelectorResolver(),
            captureProviderFactory: _ => new UnusedCaptureProvider(),
            new FakeEventOutbox(),
            new FakeTechnicalLog());

        var result = await runtime.ActivateAsync(configuration, staleApproval, CancellationToken.None);

        Assert.Equal(ActivationStatus.NotTested, result.Status);
        Assert.False(source.WasWatched);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Timed out waiting for the expected condition.");
    }

    /// <summary>
    /// A configuration whose only trigger emits an event without capturing any field, so
    /// the (fake) selector resolver and capture provider are never invoked - this test's only
    /// concern is AgentRuntime's own per-instance bookkeeping. One field is still declared
    /// (optional, unused) because ConfigurationValidator requires at least one.
    /// </summary>
    private static IntegrationConfiguration BuildConfiguration() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "agent-runtime-test-config",
        "AgentRuntime test configuration",
        new ApplicationDefinition("Fake.TestApp", "Fake Test Window"),
        [
            new FieldDefinition("unused", StageId, "Unused", Required: false, Selector: Fingerprint("Unused")),
        ],
        [
            new StageDefinition(StageId, "Stage one"),
        ],
        [
            new TriggerDefinition(
                MarkTriggerId,
                StageId,
                Fingerprint("MarkButton"),
                "Invoke",
                [new EmitEventAction("item_added")]),
        ]);

    private static ElementFingerprint Fingerprint(string automationId) =>
        new("Fake.TestApp", "Fake Test Window", AutomationId: automationId);

    private sealed class FakeApplicationInstanceSource : IApplicationInstanceSource
    {
        private readonly Channel<ApplicationInstanceChange> _channel = Channel.CreateUnbounded<ApplicationInstanceChange>();

        public void Enqueue(ApplicationInstanceChange change) => _channel.Writer.TryWrite(change);

        public IAsyncEnumerable<ApplicationInstanceChange> WatchAsync(ApplicationDefinition application, CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>
    /// Asserts <see cref="AgentRuntime.ActivateAsync"/> refuses activation before ever
    /// watching for application instances - <see cref="WasWatched"/> only ever becomes
    /// true if the gate check is bypassed (a regression this test exists to catch).
    /// </summary>
    private sealed class NeverWatchedApplicationInstanceSource : IApplicationInstanceSource
    {
        public bool WasWatched { get; private set; }

        public async IAsyncEnumerable<ApplicationInstanceChange> WatchAsync(
            ApplicationDefinition application,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            WasWatched = true;
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeTriggerProvider(Guid sessionId) : ITriggerProvider
    {
        private readonly Channel<TriggerSignal> _channel = Channel.CreateUnbounded<TriggerSignal>();

        public void FireOnce(string triggerId) =>
            _channel.Writer.TryWrite(new TriggerSignal(sessionId, triggerId, DateTimeOffset.UtcNow));

        public async IAsyncEnumerable<TriggerSignal> WatchAsync(
            TriggerDefinition trigger,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var signal in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (signal.TriggerId == trigger.Id)
                {
                    yield return signal;
                }
            }
        }
    }

    private sealed class UnusedSelectorResolver : ISelectorResolver
    {
        public Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test configuration captures no fields; the resolver must never be invoked.");
    }

    private sealed class UnusedCaptureProvider : ICaptureProvider
    {
        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test configuration captures no fields; the capture provider must never be invoked.");
    }

    private sealed class FakeEventOutbox : IEventOutbox
    {
        private readonly ConcurrentQueue<DomainEvent> _appended = new();

        public IReadOnlyList<DomainEvent> Appended => _appended.ToArray();

        public Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken)
        {
            _appended.Enqueue(domainEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DomainEvent>> ReadPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Appended);

        public Task MarkConfirmedAsync(Guid eventId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task QuarantineAsync(Guid eventId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteConfirmedBeforeAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeTechnicalLog : ITechnicalLog
    {
        public void Log(TechnicalLogEntry entry)
        {
            // Not asserted on here; SessionCoordinatorTests covers the "no captured values in
            // technical logs" guarantee in isolation.
        }
    }
}

