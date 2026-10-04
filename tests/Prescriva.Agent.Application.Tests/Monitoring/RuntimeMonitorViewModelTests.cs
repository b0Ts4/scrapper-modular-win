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
using Prescriva.Agent.Desktop.Monitoring;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Infrastructure.Events;

namespace Prescriva.Agent.Application.Tests.Monitoring;

/// <summary>
/// RuntimeMonitorViewModel is what the configurator shows while a tested configuration is
/// active: whether it is monitoring, a live Portuguese diagnostic feed, and the events
/// persisted to the outbox (with their values, decrypted for display only). Driven against
/// a real AgentRuntime fed fakes - no UI Automation, SQLite or WPF message pump.
/// </summary>
public sealed class RuntimeMonitorViewModelTests
{
    private const string StageId = "budget";

    [Fact]
    public async Task Starting_an_untested_configuration_is_refused_with_an_explanation()
    {
        var harness = new Harness();

        var status = await harness.ViewModel.StartAsync(Configuration(), approval: null);

        Assert.Equal(ActivationStatus.NotTested, status);
        Assert.False(harness.ViewModel.IsMonitoring);
        Assert.Contains("teste", harness.ViewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.InstanceSource.Watched);
    }

    [Fact]
    public async Task An_active_configuration_shows_monitoring_state_and_persisted_events_with_their_values()
    {
        var harness = new Harness();
        var configuration = Configuration();

        var status = await harness.ViewModel.StartAsync(configuration, Approve(configuration));
        Assert.Equal(ActivationStatus.Activated, status);
        Assert.True(harness.ViewModel.IsMonitoring);

        harness.InstanceSource.Start(new ApplicationInstance(Guid.NewGuid(), 4242));
        await WaitUntilAsync(() => harness.ViewModel.Diagnostics.Any(d => d.Message.Contains("Monitorando gatilho 'add_item'", StringComparison.Ordinal)));

        harness.Capture.Value = "Dipirona";
        harness.Triggers.Fire("add_item");
        await WaitUntilAsync(() => harness.ViewModel.Events.Count == 1);

        var persisted = Assert.Single(harness.ViewModel.Events);
        Assert.Equal(1, persisted.Sequence);
        Assert.Equal("item_added", persisted.Type);
        Assert.Equal(1, persisted.ItemCount);
        Assert.Contains("medication=Dipirona", persisted.FieldsText, StringComparison.Ordinal);
        Assert.Contains(harness.ViewModel.Diagnostics, d => d.Message.Contains("item_added", StringComparison.Ordinal));

        await harness.ViewModel.StopAsync();

        Assert.False(harness.ViewModel.IsMonitoring);
        Assert.Contains(harness.ViewModel.Diagnostics, d => d.Message.Contains("sessão encerrada", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_rejected_trigger_is_shown_as_an_error_naming_the_missing_field_but_never_a_value()
    {
        var harness = new Harness();
        var configuration = Configuration();
        await harness.ViewModel.StartAsync(configuration, Approve(configuration));
        harness.InstanceSource.Start(new ApplicationInstance(Guid.NewGuid(), 4242));
        await WaitUntilAsync(() => harness.ViewModel.Diagnostics.Any(d => d.Message.StartsWith("Monitorando", StringComparison.Ordinal)));

        harness.Capture.Value = "   ";
        harness.Triggers.Fire("add_item");
        await WaitUntilAsync(() => harness.ViewModel.Diagnostics.Any(d => d.IsError));

        var error = Assert.Single(harness.ViewModel.Diagnostics, d => d.IsError);
        Assert.Contains("medication", error.Message, StringComparison.Ordinal);
        Assert.Contains("obrigatório", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.ViewModel.Events);

        await harness.ViewModel.StopAsync();
    }

    [Fact]
    public async Task Diagnostics_published_concurrently_are_never_lost_from_the_displayed_list()
    {
        // Every trigger's watch becomes live at the same moment on its own thread - the
        // real situation when a session starts - and each one must stay on screen.
        const int triggerCount = 48;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness(release.Task);
        var configuration = Configuration(triggerCount);
        await harness.ViewModel.StartAsync(configuration, Approve(configuration));
        harness.InstanceSource.Start(new ApplicationInstance(Guid.NewGuid(), 4242));
        await WaitUntilAsync(() => harness.Triggers.Waiting == triggerCount);

        release.SetResult();
        await WaitUntilAsync(() => harness.Triggers.Established == triggerCount);
        await Task.Delay(100);

        var monitored = harness.ViewModel.Diagnostics.Count(d => d.Message.StartsWith("Monitorando gatilho", StringComparison.Ordinal));
        Assert.Equal(triggerCount, monitored);

        await harness.ViewModel.StopAsync();
    }

    [Fact]
    public async Task Pending_events_reaching_the_capacity_thresholds_raise_a_visible_alert_and_are_kept()
    {
        var harness = new Harness(capacityPolicy: new OutboxCapacityPolicy(warningThreshold: 2, criticalThreshold: 3));
        var configuration = Configuration();
        await harness.ViewModel.StartAsync(configuration, Approve(configuration));
        harness.InstanceSource.Start(new ApplicationInstance(Guid.NewGuid(), 4242));
        await WaitUntilAsync(() => harness.ViewModel.Diagnostics.Any(d => d.Message.StartsWith("Monitorando", StringComparison.Ordinal)));
        Assert.Null(harness.ViewModel.CapacityAlert);

        harness.Capture.Value = "Dipirona";
        for (var count = 1; count <= 3; count++)
        {
            harness.Triggers.Fire("add_item");
            var expected = count;
            await WaitUntilAsync(() => harness.ViewModel.Events.Count == expected);
            await Task.Delay(600); // past the double-click de-duplication window

            if (count == 1)
            {
                Assert.Null(harness.ViewModel.CapacityAlert);
            }
            else if (count == 2)
            {
                Assert.Contains("2 eventos pendentes", harness.ViewModel.CapacityAlert, StringComparison.Ordinal);
                Assert.False(harness.ViewModel.IsCapacityCritical);
            }
        }

        Assert.True(harness.ViewModel.IsCapacityCritical);
        Assert.Contains("crítico", harness.ViewModel.CapacityAlert, StringComparison.Ordinal);
        Assert.Equal(3, harness.Outbox.Count); // alerting never deletes pending events

        await harness.ViewModel.StopAsync();
    }

    [Fact]
    public async Task Clearing_local_data_is_refused_while_monitoring_and_empties_the_queue_when_stopped()
    {
        var cleared = 0;
        var harness = new Harness(clearLocalData: _ =>
        {
            cleared++;
            return Task.CompletedTask;
        });
        var configuration = Configuration();
        await harness.ViewModel.StartAsync(configuration, Approve(configuration));

        Assert.False(await harness.ViewModel.ClearLocalDataAsync());
        Assert.Equal(0, cleared);
        Assert.Contains("Pare o monitoramento", harness.ViewModel.StatusText, StringComparison.Ordinal);

        await harness.ViewModel.StopAsync();
        Assert.True(await harness.ViewModel.ClearLocalDataAsync());

        Assert.Equal(1, cleared);
        Assert.Contains("Dados locais apagados", harness.ViewModel.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Integration_health_is_shown_with_its_reason_and_recovers()
    {
        var harness = new Harness();
        var configuration = Configuration();
        Assert.Equal(string.Empty, harness.ViewModel.HealthText);

        await harness.ViewModel.StartAsync(configuration, Approve(configuration));
        Assert.Contains("Saudável", harness.ViewModel.HealthText, StringComparison.Ordinal);

        harness.InstanceSource.Start(new ApplicationInstance(Guid.NewGuid(), 4242));
        await WaitUntilAsync(() => harness.ViewModel.Diagnostics.Any(d => d.Message.StartsWith("Monitorando", StringComparison.Ordinal)));

        harness.Capture.Unavailable = true;
        harness.Triggers.Fire("add_item");
        await WaitUntilAsync(() => harness.ViewModel.HealthState == IntegrationHealthState.Broken);
        Assert.Contains("Quebrada", harness.ViewModel.HealthText, StringComparison.Ordinal);
        Assert.Contains("medication", harness.ViewModel.HealthText, StringComparison.Ordinal);

        await Task.Delay(600); // past the double-click de-duplication window
        harness.Capture.Unavailable = false;
        harness.Capture.Value = "Dipirona";
        harness.Triggers.Fire("add_item");
        await WaitUntilAsync(() => harness.ViewModel.HealthState == IntegrationHealthState.Healthy);
        Assert.Contains("Saudável", harness.ViewModel.HealthText, StringComparison.Ordinal);

        await harness.ViewModel.StopAsync();
    }

    private static IntegrationConfiguration Configuration(int extraTriggers = 0) => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "monitor-config",
        "Monitor configuration",
        new ApplicationDefinition("Fake.App", "Fake Window"),
        [new FieldDefinition("medication", StageId, "Medicamento", Required: true, Selector: new ElementFingerprint("Fake.App", "Fake Window", AutomationId: "Medication"))],
        [new StageDefinition(StageId, "Orçamento")],
        [
            new TriggerDefinition(
                "add_item",
                StageId,
                new ElementFingerprint("Fake.App", "Fake Window", AutomationId: "Add"),
                "Invoke",
                [new CaptureFieldsAction(["medication"]), new EmitEventAction("item_added")]),
            .. Enumerable.Range(1, extraTriggers - (extraTriggers > 0 ? 1 : 0)).Select(index => new TriggerDefinition(
                $"button_{index}",
                StageId,
                new ElementFingerprint("Fake.App", "Fake Window", AutomationId: $"Button{index}"),
                "Invoke",
                [new EmitEventAction("noop")])),
        ]);

    private static ConfigurationApproval Approve(IntegrationConfiguration configuration) =>
        new(configuration.Id, ConfigurationFingerprint.Compute(configuration), DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "Timed out waiting for the expected view model state.");
    }

    private sealed class Harness
    {
        public Harness(
            Task? watchGate = null,
            OutboxCapacityPolicy? capacityPolicy = null,
            Func<CancellationToken, Task>? clearLocalData = null)
        {
            Triggers = new FakeTriggers(watchGate ?? Task.CompletedTask);
            var runtime = new AgentRuntime(
                InstanceSource,
                (_, sessionId) => Triggers.ForSession(sessionId),
                _ => new FoundResolver(),
                _ => Capture,
                Outbox,
                new NullLog());
            ViewModel = new RuntimeMonitorViewModel(runtime, Outbox, capacityPolicy: capacityPolicy, clearLocalData: clearLocalData);
        }

        public FakeInstanceSource InstanceSource { get; } = new();
        public FakeTriggers Triggers { get; }
        public FakeCapture Capture { get; } = new();
        public InMemoryOutbox Outbox { get; } = new();
        public RuntimeMonitorViewModel ViewModel { get; }
    }

    private sealed class FakeInstanceSource : IApplicationInstanceSource
    {
        private readonly Channel<ApplicationInstanceChange> _changes = Channel.CreateUnbounded<ApplicationInstanceChange>();

        public ConcurrentQueue<ApplicationDefinition> Watched { get; } = new();

        public void Start(ApplicationInstance instance) =>
            _changes.Writer.TryWrite(new ApplicationInstanceChange(instance, ApplicationInstanceChangeKind.Started));

        public async IAsyncEnumerable<ApplicationInstanceChange> WatchAsync(
            ApplicationDefinition application,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Watched.Enqueue(application);
            await foreach (var change in _changes.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }
    }

    private sealed class FakeTriggers(Task watchGate)
    {
        private readonly ConcurrentBag<Provider> _providers = new();
        private int _waiting;
        private int _established;

        public Task WatchGate => watchGate;

        public int Waiting => Volatile.Read(ref _waiting);

        public int Established => Volatile.Read(ref _established);

        public ITriggerProvider ForSession(Guid sessionId)
        {
            var provider = new Provider(sessionId, this);
            _providers.Add(provider);
            return provider;
        }

        public void Fire(string triggerId)
        {
            foreach (var provider in _providers)
            {
                provider.Fire(triggerId);
            }
        }

        private sealed class Provider(Guid sessionId, FakeTriggers owner) : ITriggerProvider
        {
            private readonly Channel<TriggerSignal> _signals = Channel.CreateUnbounded<TriggerSignal>();

            public event EventHandler<string>? WatchEstablished;

            public void Fire(string triggerId) =>
                _signals.Writer.TryWrite(new TriggerSignal(sessionId, triggerId, DateTimeOffset.UtcNow, Guid.NewGuid().ToString()));

            public async IAsyncEnumerable<TriggerSignal> WatchAsync(
                TriggerDefinition trigger,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._waiting);
                await owner.WatchGate.ConfigureAwait(false);
                await Task.Yield();
                WatchEstablished?.Invoke(this, trigger.Id);
                Interlocked.Increment(ref owner._established);
                await foreach (var signal in _signals.Reader.ReadAllAsync(cancellationToken))
                {
                    if (signal.TriggerId == trigger.Id)
                    {
                        yield return signal;
                    }
                }
            }
        }
    }

    private sealed class FoundResolver : ISelectorResolver
    {
        public Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken) =>
            Task.FromResult(SelectorResolution.Found(Handle.Instance, 0.97));

        private sealed class Handle : ResolvedElementHandle
        {
            public static readonly Handle Instance = new();
        }
    }

    private sealed class FakeCapture : ICaptureProvider
    {
        public string? Value { get; set; }

        public bool Unavailable { get; set; }

        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken) =>
            Task.FromResult(Unavailable
                ? new CaptureResult(CaptureOutcome.ElementUnavailable, null, CaptureResult.UiaProviderId, 0, TimeSpan.Zero, [])
                : new CaptureResult(CaptureOutcome.Captured, Value, CaptureResult.UiaProviderId, 1.0, TimeSpan.Zero, []));
    }

    private sealed class InMemoryOutbox : IEventOutbox
    {
        private readonly ConcurrentQueue<DomainEvent> _events = new();

        public int Count => _events.Count;

        public Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken)
        {
            _events.Enqueue(domainEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DomainEvent>> ReadPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DomainEvent>>(_events.ToArray());

        public Task MarkConfirmedAsync(Guid eventId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task QuarantineAsync(Guid eventId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteConfirmedBeforeAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NullLog : ITechnicalLog
    {
        public void Log(TechnicalLogEntry entry)
        {
        }
    }
}
