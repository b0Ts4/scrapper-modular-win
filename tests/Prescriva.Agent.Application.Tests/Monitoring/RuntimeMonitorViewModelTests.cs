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

    private static IntegrationConfiguration Configuration() => new(
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
        public Harness()
        {
            var runtime = new AgentRuntime(
                InstanceSource,
                (_, sessionId) => Triggers.ForSession(sessionId),
                _ => new FoundResolver(),
                _ => Capture,
                Outbox,
                new NullLog());
            ViewModel = new RuntimeMonitorViewModel(runtime, Outbox);
        }

        public FakeInstanceSource InstanceSource { get; } = new();
        public FakeTriggers Triggers { get; } = new();
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

    private sealed class FakeTriggers
    {
        private readonly ConcurrentBag<Provider> _providers = new();

        public ITriggerProvider ForSession(Guid sessionId)
        {
            var provider = new Provider(sessionId);
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

        private sealed class Provider(Guid sessionId) : ITriggerProvider
        {
            private readonly Channel<TriggerSignal> _signals = Channel.CreateUnbounded<TriggerSignal>();

            public event EventHandler<string>? WatchEstablished;

            public void Fire(string triggerId) =>
                _signals.Writer.TryWrite(new TriggerSignal(sessionId, triggerId, DateTimeOffset.UtcNow, Guid.NewGuid().ToString()));

            public async IAsyncEnumerable<TriggerSignal> WatchAsync(
                TriggerDefinition trigger,
                [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await Task.Yield();
                WatchEstablished?.Invoke(this, trigger.Id);
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

        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken) =>
            Task.FromResult(new CaptureResult(CaptureOutcome.Captured, Value, CaptureResult.UiaProviderId, 1.0, TimeSpan.Zero, []));
    }

    private sealed class InMemoryOutbox : IEventOutbox
    {
        private readonly ConcurrentQueue<DomainEvent> _events = new();

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
