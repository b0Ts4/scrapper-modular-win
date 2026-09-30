using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Diagnostics;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Processes;

namespace Prescriva.Agent.Windows.IntegrationTests.Runtime;

/// <summary>
/// Covers this plan's review focus item directly: two running instances of the same
/// configured application must maintain independent capture sessions. Launches two real,
/// separately started Prescriva.Agent.TestTarget.exe processes and drives them through the
/// real <see cref="WindowsApplicationInstanceSource"/> (the only genuinely new piece of
/// production code this task adds that process/window enumeration touches) and the real
/// <see cref="AgentRuntime"/>/<see cref="SessionCoordinator"/> orchestration.
///
/// The trigger provider, selector resolver and capture provider are fakes, not real UI
/// Automation: the configuration used here deliberately captures no fields (its only
/// trigger just emits an event), so the resolver/capture fakes are never even invoked -
/// this keeps the test's only "real" moving part the one this task actually adds
/// (process/window discovery and per-instance session orchestration), while avoiding a
/// known pre-existing limitation of <c>UiAutomationSelectorResolver.Find</c>, which matches
/// windows by process name and title only (no process ID), so it cannot itself tell two
/// identically titled TestTarget instances apart - a separate concern outside this task's
/// files.
/// </summary>
public sealed class MultipleInstanceTests
{
    private static readonly ApplicationDefinition TestTargetApplication =
        new("Prescriva.Agent.TestTarget", "Prescriva Agent Test Target");

    [Fact]
    public async Task WindowsApplicationInstanceSource_reports_two_distinct_started_instances_for_two_real_processes()
    {
        using var first = TestTargetLauncher.Launch();
        using var second = TestTargetLauncher.Launch();

        var firstProcessId = first.Window.Current.ProcessId;
        var secondProcessId = second.Window.Current.ProcessId;

        var source = new WindowsApplicationInstanceSource(pollInterval: TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var seenProcessIds = new HashSet<int>();
        var seenInstanceIds = new HashSet<Guid>();

        await foreach (var change in source.WatchAsync(TestTargetApplication, cts.Token).WithCancellation(cts.Token))
        {
            if (change.Kind != ApplicationInstanceChangeKind.Started)
            {
                continue;
            }

            if (change.Instance.ProcessId != firstProcessId && change.Instance.ProcessId != secondProcessId)
            {
                // Some other Prescriva.Agent.TestTarget instance happens to be running on
                // this machine outside this test - ignore it.
                continue;
            }

            seenProcessIds.Add(change.Instance.ProcessId);
            seenInstanceIds.Add(change.Instance.InstanceId);

            if (seenProcessIds.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(2, seenProcessIds.Count);
        Assert.Equal(2, seenInstanceIds.Count); // distinct instance IDs - never collapsed into one
        Assert.Contains(firstProcessId, seenProcessIds);
        Assert.Contains(secondProcessId, seenProcessIds);
    }

    [Fact]
    public async Task AgentRuntime_keeps_two_real_application_instances_sessions_fully_independent()
    {
        using var first = TestTargetLauncher.Launch();
        using var second = TestTargetLauncher.Launch();

        var firstProcessId = first.Window.Current.ProcessId;
        var secondProcessId = second.Window.Current.ProcessId;

        var configuration = BuildConfiguration();
        var outbox = new FakeEventOutbox();
        var log = new FakeTechnicalLog();
        var triggerProvidersByProcessId = new ConcurrentDictionary<int, FakeTriggerProvider>();

        var runtime = new AgentRuntime(
            new WindowsApplicationInstanceSource(pollInterval: TimeSpan.FromMilliseconds(100)),
            triggerProviderFactory: (instance, sessionId) =>
            {
                var provider = new FakeTriggerProvider(instance, sessionId);
                triggerProvidersByProcessId[instance.ProcessId] = provider;
                return provider;
            },
            selectorResolverFactory: _ => new UnusedSelectorResolver(),
            captureProviderFactory: _ => new UnusedCaptureProvider(),
            outbox,
            log);

        using var activationCts = new CancellationTokenSource();
        var activateTask = runtime.ActivateAsync(configuration, activationCts.Token);

        try
        {
            // Wait for AgentRuntime to discover both real instances and build their
            // per-instance (fake) trigger providers.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline &&
                   !(triggerProvidersByProcessId.ContainsKey(firstProcessId) && triggerProvidersByProcessId.ContainsKey(secondProcessId)))
            {
                await Task.Delay(100);
            }

            Assert.True(triggerProvidersByProcessId.ContainsKey(firstProcessId), "AgentRuntime never discovered the first instance.");
            Assert.True(triggerProvidersByProcessId.ContainsKey(secondProcessId), "AgentRuntime never discovered the second instance.");

            // Fire only the first instance's trigger, and confirm only its own session
            // advances - the second instance's session must be completely unaffected.
            triggerProvidersByProcessId[firstProcessId].FireOnce();

            await WaitUntilAsync(() => outbox.Appended.Count >= 1, TimeSpan.FromSeconds(10));
            Assert.Single(outbox.Appended);

            var firstSessionId = triggerProvidersByProcessId[firstProcessId].SessionId;
            var secondSessionId = triggerProvidersByProcessId[secondProcessId].SessionId;
            Assert.NotEqual(firstSessionId, secondSessionId);
            Assert.Equal(firstSessionId, outbox.Appended[0].SessionId);
            Assert.Equal(1, outbox.Appended[0].Sequence);

            // Now fire the second instance's trigger - it must get its own independent
            // sequence starting again at 1, not continuing the first instance's sequence.
            triggerProvidersByProcessId[secondProcessId].FireOnce();

            await WaitUntilAsync(() => outbox.Appended.Count >= 2, TimeSpan.FromSeconds(10));
            Assert.Equal(2, outbox.Appended.Count);

            var secondInstanceEvent = outbox.Appended.Single(e => e.SessionId == secondSessionId);
            Assert.Equal(1, secondInstanceEvent.Sequence); // independent sequence, not "3"

            // The first instance's session must be untouched by the second instance firing.
            var firstInstanceEvents = outbox.Appended.Where(e => e.SessionId == firstSessionId).ToList();
            Assert.Single(firstInstanceEvents);
            Assert.Equal(1, firstInstanceEvents[0].Sequence);
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.True(condition(), "Timed out waiting for the expected condition.");
    }

    /// <summary>
    /// A configuration whose only trigger emits an event without capturing any field, so
    /// the (fake) selector resolver and capture provider are never invoked - this test's
    /// only real moving parts are process/window discovery and per-instance session
    /// orchestration. One field is still declared (optional, unused) because
    /// ConfigurationValidator requires at least one.
    /// </summary>
    private static IntegrationConfiguration BuildConfiguration() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "multi-instance-config",
        "Multiple instance test configuration",
        TestTargetApplication,
        [
            new FieldDefinition("unused", "stage-1", "Unused", Required: false, Selector: Fingerprint("Unused")),
        ],
        [
            new StageDefinition("stage-1", "Stage one"),
        ],
        [
            new TriggerDefinition(
                "mark",
                "stage-1",
                Fingerprint("MarkButton"),
                "Invoke",
                [new EmitEventAction("item_added")]),
        ]);

    private static ElementFingerprint Fingerprint(string automationId) =>
        new("Prescriva.Agent.TestTarget", "Prescriva Agent Test Target", AutomationId: automationId);

    /// <summary>
    /// Yields exactly one <see cref="TriggerSignal"/> for the "mark" trigger when
    /// <see cref="FireOnce"/> is called, and never yields for any other trigger. Built by
    /// the <see cref="TriggerProviderFactory"/> once per discovered application instance, so
    /// each real process gets its own independent provider/channel.
    /// </summary>
    private sealed class FakeTriggerProvider(ApplicationInstance instance, Guid sessionId) : ITriggerProvider
    {
        private readonly System.Threading.Channels.Channel<TriggerSignal> _channel =
            System.Threading.Channels.Channel.CreateUnbounded<TriggerSignal>();

        public Guid SessionId => sessionId;

        public void FireOnce() =>
            _channel.Writer.TryWrite(new TriggerSignal(sessionId, "mark", DateTimeOffset.UtcNow, instance.InstanceId.ToString()));

        public async IAsyncEnumerable<TriggerSignal> WatchAsync(
            TriggerDefinition trigger,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (trigger.Id != "mark")
            {
                yield break;
            }

            await foreach (var signal in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return signal;
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
            // Not asserted on in this test; SessionCoordinatorTests already covers the
            // "no captured values in technical logs" guarantee in isolation.
        }
    }
}
