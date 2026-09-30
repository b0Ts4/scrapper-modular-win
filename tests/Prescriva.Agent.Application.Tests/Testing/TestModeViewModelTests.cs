using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Desktop.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Tests.Testing;

/// <summary>
/// Exercises TestModeViewModel against a real IntegrationTestRunner fed fake
/// ISelectorResolver/ICaptureProvider/ITriggerProvider - no real UI Automation and no real
/// WPF message pump involved (the view model's dispatcher check-access short-circuits to
/// synchronous execution on this single test thread, the same way InspectorViewModel's own
/// tests rely on).
/// </summary>
public sealed class TestModeViewModelTests
{
    private const string StageId = "stage-1";
    private const string FoundFieldId = "medication_name";
    private const string MissingFieldId = "dose";
    private const string AmbiguousFieldId = "note";
    private const string DetectedTriggerId = "add_button";
    private const string TimedOutTriggerId = "finish_button";

    [Fact]
    public async Task RunAsync_a_fully_passing_configuration_reports_found_and_detected_in_portuguese_and_allows_approval()
    {
        var configuration = BuildPassingConfiguration();
        var runner = BuildRunner(passing: true);
        var viewModel = new TestModeViewModel(runner);

        await viewModel.RunAsync(configuration);

        var field = Assert.Single(viewModel.FieldResults);
        Assert.True(field.Passed);
        Assert.Equal("Encontrado", field.StatusText);
        Assert.Equal("uia", field.ProviderText);
        Assert.Null(field.FailureMessage);

        var trigger = Assert.Single(viewModel.TriggerResults);
        Assert.True(trigger.Passed);
        Assert.Equal("Detectado", trigger.StatusText);
        Assert.Null(trigger.FailureMessage);

        Assert.True(viewModel.CanApprove);
    }

    [Fact]
    public async Task RunAsync_a_not_found_field_reports_actionable_portuguese_text_and_blocks_approval()
    {
        var configuration = BuildFailingConfiguration();
        var runner = BuildRunner(passing: false);
        var viewModel = new TestModeViewModel(runner);

        await viewModel.RunAsync(configuration);

        var field = Assert.Single(viewModel.FieldResults, f => f.FieldId == MissingFieldId);
        Assert.False(field.Passed);
        Assert.Equal("Não encontrado", field.StatusText);
        Assert.NotNull(field.FailureMessage);
        Assert.Contains("não encontrado", field.FailureMessage, StringComparison.OrdinalIgnoreCase);

        Assert.False(viewModel.CanApprove);
    }

    [Fact]
    public async Task RunAsync_an_ambiguous_field_reports_actionable_portuguese_text()
    {
        var configuration = BuildFailingConfiguration();
        var runner = BuildRunner(passing: false);
        var viewModel = new TestModeViewModel(runner);

        await viewModel.RunAsync(configuration);

        var field = Assert.Single(viewModel.FieldResults, f => f.FieldId == AmbiguousFieldId);
        Assert.Equal("Ambíguo", field.StatusText);
        Assert.Contains("ambígua", field.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_a_trigger_that_times_out_reports_actionable_portuguese_text()
    {
        var configuration = BuildFailingConfiguration();
        var runner = BuildRunner(passing: false);
        var viewModel = new TestModeViewModel(runner);

        await viewModel.RunAsync(configuration);

        var trigger = Assert.Single(viewModel.TriggerResults, t => t.TriggerId == TimedOutTriggerId);
        Assert.Equal("Tempo esgotado", trigger.StatusText);
        Assert.Contains("tempo limite", trigger.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Approve_throws_before_any_run()
    {
        var runner = BuildRunner(passing: true);
        var viewModel = new TestModeViewModel(runner);

        Assert.Throws<InvalidOperationException>(() => viewModel.Approve());
    }

    [Fact]
    public async Task Approve_throws_when_the_most_recent_run_failed()
    {
        var configuration = BuildFailingConfiguration();
        var runner = BuildRunner(passing: false);
        var viewModel = new TestModeViewModel(runner);

        await viewModel.RunAsync(configuration);

        Assert.Throws<InvalidOperationException>(() => viewModel.Approve());
    }

    [Fact]
    public async Task Approve_after_a_fully_passing_run_produces_an_approval_bound_to_the_configurations_fingerprint()
    {
        var configuration = BuildPassingConfiguration();
        var runner = BuildRunner(passing: true);
        var viewModel = new TestModeViewModel(runner);

        await viewModel.RunAsync(configuration);
        var approval = viewModel.Approve();

        Assert.Equal(configuration.Id, approval.ConfigurationId);
        Assert.Equal(ConfigurationFingerprint.Compute(configuration), approval.Fingerprint);
        Assert.Same(approval, viewModel.LastApproval);
    }

    [Fact]
    public async Task A_new_run_clears_the_previous_approval()
    {
        var configuration = BuildPassingConfiguration();
        var runner = BuildRunner(passing: true);
        var viewModel = new TestModeViewModel(runner);

        await viewModel.RunAsync(configuration);
        viewModel.Approve();
        Assert.NotNull(viewModel.LastApproval);

        await viewModel.RunAsync(configuration);
        Assert.Null(viewModel.LastApproval);
    }

    private static IntegrationTestRunner BuildRunner(bool passing)
    {
        var resolver = passing
            ? new FakeSelectorResolver(new()
            {
                [FoundFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.9),
            })
            : new FakeSelectorResolver(new()
            {
                [FoundFieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.9),
                [MissingFieldId] = SelectorResolution.NotFound("gone"),
                [AmbiguousFieldId] = SelectorResolution.Ambiguous(0.5),
            });

        var captureProvider = new FakeCaptureProvider(new()
        {
            [FoundFieldId] = new CaptureResult(CaptureOutcome.Captured, "Dipirona", "uia", 0.9, TimeSpan.Zero, []),
        });

        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(DetectedTriggerId);
        // TimedOutTriggerId (used only by the failing configuration) is deliberately never fired.

        return new IntegrationTestRunner(resolver, captureProvider, triggerProvider, triggerTimeout: TimeSpan.FromMilliseconds(100));
    }

    private static IntegrationConfiguration BuildPassingConfiguration() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "test-mode-vm-passing",
        "Test mode view model passing configuration",
        new ApplicationDefinition("Fake.TestApp", "Fake Test Window"),
        [
            new FieldDefinition(FoundFieldId, StageId, "Medication name", Required: true, Selector: Fingerprint(FoundFieldId)),
        ],
        [new StageDefinition(StageId, "Stage one")],
        [
            new TriggerDefinition(DetectedTriggerId, StageId, Fingerprint(DetectedTriggerId), "Invoke", [new EmitEventAction("item_added")]),
        ]);

    private static IntegrationConfiguration BuildFailingConfiguration() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "test-mode-vm-failing",
        "Test mode view model failing configuration",
        new ApplicationDefinition("Fake.TestApp", "Fake Test Window"),
        [
            new FieldDefinition(FoundFieldId, StageId, "Medication name", Required: true, Selector: Fingerprint(FoundFieldId)),
            new FieldDefinition(MissingFieldId, StageId, "Dose", Required: false, Selector: Fingerprint(MissingFieldId)),
            new FieldDefinition(AmbiguousFieldId, StageId, "Note", Required: false, Selector: Fingerprint(AmbiguousFieldId)),
        ],
        [new StageDefinition(StageId, "Stage one")],
        [
            new TriggerDefinition(DetectedTriggerId, StageId, Fingerprint(DetectedTriggerId), "Invoke", [new EmitEventAction("item_added")]),
            new TriggerDefinition(TimedOutTriggerId, StageId, Fingerprint(TimedOutTriggerId), "Invoke", [new FinishSessionAction()]),
        ]);

    private static ElementFingerprint Fingerprint(string automationId) =>
        new("Fake.TestApp", "Fake Test Window", AutomationId: automationId);

    private sealed class FakeSelectorResolver(Dictionary<string, SelectorResolution> resolutionsByAutomationId) : ISelectorResolver
    {
        public Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken)
        {
            var key = fingerprint.AutomationId ?? string.Empty;
            return Task.FromResult(resolutionsByAutomationId.TryGetValue(key, out var resolution)
                ? resolution
                : SelectorResolution.NotFound("no fake resolution configured"));
        }
    }

    private sealed class FakeCaptureProvider(Dictionary<string, CaptureResult> resultsByFieldId) : ICaptureProvider
    {
        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken) =>
            Task.FromResult(resultsByFieldId.TryGetValue(field.Id, out var result)
                ? result
                : new CaptureResult(CaptureOutcome.ElementUnavailable, null, "fake", 0, TimeSpan.Zero, []));
    }

    private sealed class FakeResolvedElementHandle : ResolvedElementHandle
    {
        public static readonly FakeResolvedElementHandle Instance = new();
    }

    private sealed class FakeTriggerProvider : ITriggerProvider
    {
        private readonly Channel<TriggerSignal> _channel = Channel.CreateUnbounded<TriggerSignal>();

        public void FireOnce(string triggerId) =>
            _channel.Writer.TryWrite(new TriggerSignal(Guid.NewGuid(), triggerId, DateTimeOffset.UtcNow));

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
}
