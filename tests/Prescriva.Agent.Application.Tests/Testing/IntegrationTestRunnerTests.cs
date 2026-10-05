using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Tests.Testing;

/// <summary>
/// Exercises IntegrationTestRunner and the ConfigurationFingerprint/ConfigurationApproval
/// gate it feeds, against fake ISelectorResolver/ICaptureProvider/ITriggerProvider - no real
/// UI Automation involved anywhere in this file, following the same pattern established by
/// InspectionControllerTests and AgentRuntimeTests.
/// </summary>
public sealed class IntegrationTestRunnerTests
{
    private const string StageId = "stage-1";
    private const string FieldId = "medication_name";
    private const string TriggerId = "add_button";

    [Fact]
    public async Task A_found_field_reports_the_provider_and_confidence_that_served_it()
    {
        var resolver = new FakeSelectorResolver(new()
        {
            [FieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
        });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [FieldId] = new CaptureResult(CaptureOutcome.Captured, "Dipirona", "uia", 0.87, TimeSpan.FromMilliseconds(5), []),
        });
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);

        var runner = new IntegrationTestRunner(resolver, captureProvider, triggerProvider);
        var report = await runner.RunAsync(BuildConfiguration());

        var fieldResult = Assert.Single(report.FieldResults);
        Assert.Equal(FieldCheckOutcome.Found, fieldResult.Outcome);
        Assert.Equal("uia", fieldResult.ProviderId);
        Assert.Equal(0.87, fieldResult.Confidence);
        Assert.Null(fieldResult.FailureCode);
    }

    [Fact]
    public async Task A_found_field_reports_the_matched_signals_the_lead_the_duration_and_no_warning_when_strong()
    {
        var evidence = System.Collections.Immutable.ImmutableDictionary<string, int>.Empty.Add("automationId", 40).Add("controlType", 15);
        var resolver = new FakeSelectorResolver(
            new() { [FieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 1.0, evidence, lead: 55) },
            delay: TimeSpan.FromMilliseconds(60));
        var captureProvider = new FakeCaptureProvider(new()
        {
            [FieldId] = new CaptureResult(CaptureOutcome.Captured, "Dipirona", "uia", 1.0, TimeSpan.FromMilliseconds(5), []),
        });
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);

        var field = Assert.Single((await new IntegrationTestRunner(resolver, captureProvider, triggerProvider).RunAsync(BuildConfiguration())).FieldResults);

        Assert.Equal(evidence, field.Signals);
        Assert.Equal(55, field.Lead);
        Assert.True(field.Duration >= TimeSpan.FromMilliseconds(50), $"Duration {field.Duration} does not include the resolution.");
        Assert.Empty(field.Warnings);
    }

    [Fact]
    public async Task A_field_found_without_AutomationId_and_with_a_narrow_lead_reports_fragility_warnings()
    {
        var evidence = System.Collections.Immutable.ImmutableDictionary<string, int>.Empty.Add("nearbyLabels", 20).Add("controlType", 15).Add("ancestors", 6);
        var resolver = new FakeSelectorResolver(new() { [string.Empty] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.9, evidence, lead: 12) });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [FieldId] = new CaptureResult(CaptureOutcome.Captured, "nota", "uia", 1.0, TimeSpan.Zero, []),
        });
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);
        var configuration = BuildConfiguration();
        var labelOnly = new ElementFingerprint("Fake.TestApp", "Fake Test Window", ControlType: "ControlType.Edit", NearbyLabels: ["Observações:"]);
        configuration = configuration with { Fields = [configuration.Fields[0] with { Selector = labelOnly }] };

        var field = Assert.Single((await new IntegrationTestRunner(resolver, captureProvider, triggerProvider).RunAsync(configuration)).FieldResults);

        Assert.Equal(FieldCheckOutcome.Found, field.Outcome);
        Assert.Equal([SelectorFragilityWarning.MissingAutomationId, SelectorFragilityWarning.NarrowLead], field.Warnings.ToArray());
    }

    [Fact]
    public async Task A_trigger_reports_how_long_it_took_to_be_detected()
    {
        var resolver = new FakeSelectorResolver(new() { [FieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 1.0) });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [FieldId] = new CaptureResult(CaptureOutcome.Captured, "Dipirona", "uia", 1.0, TimeSpan.Zero, []),
        });
        var triggerProvider = new FakeTriggerProvider();

        var run = new IntegrationTestRunner(resolver, captureProvider, triggerProvider).RunAsync(BuildConfiguration());
        await Task.Delay(200);
        triggerProvider.FireOnce(TriggerId);
        var trigger = Assert.Single((await run).TriggerResults);

        Assert.Equal(TriggerCheckOutcome.Detected, trigger.Outcome);
        Assert.InRange(trigger.Duration, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_found_field_whose_value_cannot_be_read_fails_the_test_with_a_typed_code()
    {
        var resolver = new FakeSelectorResolver(new() { [FieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95) });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [FieldId] = new CaptureResult(CaptureOutcome.TooLarge, null, "uia", 0, TimeSpan.Zero, []),
        });
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);

        var report = await new IntegrationTestRunner(resolver, captureProvider, triggerProvider, triggerTimeout: TimeSpan.FromSeconds(2)).RunAsync(BuildConfiguration());

        var field = Assert.Single(report.FieldResults);
        Assert.Equal(FieldCheckOutcome.Unreadable, field.Outcome);
        Assert.Equal(FieldCheckResult.FieldUnreadableCode, field.FailureCode);
        Assert.Equal(CaptureOutcome.TooLarge, field.CaptureOutcome);
        Assert.False(report.AllPassed);
    }

    [Fact]
    public async Task A_file_field_reports_the_captured_attachment_kept_in_memory()
    {
        var resolver = new FakeSelectorResolver(new() { [FieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95) });
        var attachment = new CapturedAttachment([1, 2, 3], "receita.pdf", "application/pdf", AttachmentSource.File);
        var captureProvider = new FakeCaptureProvider(new()
        {
            [FieldId] = new CaptureResult(CaptureOutcome.Captured, null, "uia", 1.0, TimeSpan.Zero, [], attachment),
        });
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);
        var configuration = BuildConfiguration();
        configuration = configuration with { Fields = [configuration.Fields[0] with { Kind = FieldKind.File }] };

        var report = await new IntegrationTestRunner(resolver, captureProvider, triggerProvider, triggerTimeout: TimeSpan.FromSeconds(2)).RunAsync(configuration);

        var field = Assert.Single(report.FieldResults);
        Assert.Equal(FieldCheckOutcome.Found, field.Outcome);
        Assert.Same(attachment, field.Attachment);
        Assert.True(report.AllPassed);
    }

    [Fact]
    public async Task A_not_found_field_reports_the_not_found_failure_code_and_never_calls_capture()
    {
        var resolver = new FakeSelectorResolver(new()
        {
            [FieldId] = SelectorResolution.NotFound("no candidate scored high enough"),
        });
        var captureProvider = new UnusedCaptureProvider();
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);

        var runner = new IntegrationTestRunner(resolver, captureProvider, triggerProvider);
        var report = await runner.RunAsync(BuildConfiguration());

        var fieldResult = Assert.Single(report.FieldResults);
        Assert.Equal(FieldCheckOutcome.NotFound, fieldResult.Outcome);
        Assert.Null(fieldResult.ProviderId);
        Assert.Equal(FieldCheckResult.FieldNotFoundCode, fieldResult.FailureCode);
    }

    [Fact]
    public async Task An_ambiguous_field_reports_the_ambiguous_failure_code()
    {
        var resolver = new FakeSelectorResolver(new()
        {
            [FieldId] = SelectorResolution.Ambiguous(0.6),
        });
        var captureProvider = new UnusedCaptureProvider();
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);

        var runner = new IntegrationTestRunner(resolver, captureProvider, triggerProvider);
        var report = await runner.RunAsync(BuildConfiguration());

        var fieldResult = Assert.Single(report.FieldResults);
        Assert.Equal(FieldCheckOutcome.Ambiguous, fieldResult.Outcome);
        Assert.Equal(FieldCheckResult.FieldAmbiguousCode, fieldResult.FailureCode);
    }

    [Fact]
    public async Task A_trigger_that_fires_is_reported_as_detected_with_its_declared_transitions_and_events()
    {
        var resolver = new FakeSelectorResolver(new()
        {
            [FieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
        });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [FieldId] = new CaptureResult(CaptureOutcome.Captured, "Dipirona", "uia", 0.9, TimeSpan.Zero, []),
        });
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);

        var runner = new IntegrationTestRunner(resolver, captureProvider, triggerProvider, triggerTimeout: TimeSpan.FromSeconds(2));
        var report = await runner.RunAsync(BuildConfiguration());

        var triggerResult = Assert.Single(report.TriggerResults);
        Assert.Equal(TriggerCheckOutcome.Detected, triggerResult.Outcome);
        Assert.Null(triggerResult.FailureCode);
        Assert.Contains("stage-2", triggerResult.StageTransitions);
        Assert.Contains("item_added", triggerResult.EmittedEventTypes);
    }

    [Fact]
    public async Task A_trigger_that_never_fires_within_the_wait_window_is_reported_as_timed_out()
    {
        var resolver = new FakeSelectorResolver(new()
        {
            [FieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
        });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [FieldId] = new CaptureResult(CaptureOutcome.Captured, "Dipirona", "uia", 0.9, TimeSpan.Zero, []),
        });
        var triggerProvider = new FakeTriggerProvider(); // never fires

        var runner = new IntegrationTestRunner(resolver, captureProvider, triggerProvider, triggerTimeout: TimeSpan.FromMilliseconds(50));
        var report = await runner.RunAsync(BuildConfiguration());

        var triggerResult = Assert.Single(report.TriggerResults);
        Assert.Equal(TriggerCheckOutcome.TimedOut, triggerResult.Outcome);
        Assert.Equal(TriggerCheckResult.TriggerTimedOutCode, triggerResult.FailureCode);
    }

    [Fact]
    public async Task A_run_where_every_field_and_trigger_pass_produces_an_approval_bound_to_the_configurations_fingerprint()
    {
        var configuration = BuildConfiguration();
        var resolver = new FakeSelectorResolver(new()
        {
            [FieldId] = SelectorResolution.Found(FakeResolvedElementHandle.Instance, 0.95),
        });
        var captureProvider = new FakeCaptureProvider(new()
        {
            [FieldId] = new CaptureResult(CaptureOutcome.Captured, "Dipirona", "uia", 0.9, TimeSpan.Zero, []),
        });
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);

        var runner = new IntegrationTestRunner(resolver, captureProvider, triggerProvider, triggerTimeout: TimeSpan.FromSeconds(2));
        var report = await runner.RunAsync(configuration);

        Assert.True(report.AllPassed);
        var approval = report.ToApproval(DateTimeOffset.UtcNow);
        Assert.NotNull(approval);
        Assert.Equal(configuration.Id, approval!.ConfigurationId);
        Assert.Equal(ConfigurationFingerprint.Compute(configuration), approval.Fingerprint);
        Assert.True(approval.IsValidFor(ConfigurationFingerprint.Compute(configuration)));
    }

    [Fact]
    public async Task A_run_with_any_failing_field_or_trigger_never_produces_an_approval()
    {
        var configuration = BuildConfiguration();
        var resolver = new FakeSelectorResolver(new()
        {
            [FieldId] = SelectorResolution.NotFound("gone"),
        });
        var captureProvider = new UnusedCaptureProvider();
        var triggerProvider = new FakeTriggerProvider();
        triggerProvider.FireOnce(TriggerId);

        var runner = new IntegrationTestRunner(resolver, captureProvider, triggerProvider, triggerTimeout: TimeSpan.FromSeconds(2));
        var report = await runner.RunAsync(configuration);

        Assert.False(report.AllPassed);
        Assert.Null(report.ToApproval(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Editing_the_configuration_after_approval_invalidates_that_approval()
    {
        var original = BuildConfiguration();
        var approval = new ConfigurationApproval(original.Id, ConfigurationFingerprint.Compute(original), DateTimeOffset.UtcNow);

        // A semantically tiny change - renaming the stage - still changes the configuration's
        // meaning, so it must still invalidate the approval. Conservative by design: any
        // content change at all invalidates, never just a curated subset of "important" ones.
        var edited = original with
        {
            Stages = original.Stages.SetItem(0, original.Stages[0] with { Name = original.Stages[0].Name + " " }),
        };

        Assert.True(approval.IsValidFor(ConfigurationFingerprint.Compute(original)));
        Assert.False(approval.IsValidFor(ConfigurationFingerprint.Compute(edited)));
    }

    [Fact]
    public void Two_computations_over_identical_content_produce_the_identical_fingerprint()
    {
        var first = BuildConfiguration();
        var second = BuildConfiguration();

        Assert.Equal(ConfigurationFingerprint.Compute(first), ConfigurationFingerprint.Compute(second));
    }

    private static IntegrationConfiguration BuildConfiguration() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "test-mode-config",
        "Test mode configuration",
        new ApplicationDefinition("Fake.TestApp", "Fake Test Window"),
        [
            new FieldDefinition(FieldId, StageId, "Medication name", Required: true, Selector: Fingerprint(FieldId)),
        ],
        [
            new StageDefinition(StageId, "Stage one"),
            new StageDefinition("stage-2", "Stage two"),
        ],
        [
            new TriggerDefinition(
                TriggerId,
                StageId,
                Fingerprint(TriggerId),
                "Invoke",
                [
                    new CaptureFieldsAction([FieldId]),
                    new TransitionStageAction("stage-2"),
                    new EmitEventAction("item_added"),
                ]),
        ]);

    private static ElementFingerprint Fingerprint(string automationId) =>
        new("Fake.TestApp", "Fake Test Window", AutomationId: automationId);

    private sealed class FakeSelectorResolver(Dictionary<string, SelectorResolution> resolutionsByAutomationId, TimeSpan? delay = null) : ISelectorResolver
    {
        public async Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken)
        {
            if (delay is { } wait) await Task.Delay(wait, cancellationToken);
            var key = fingerprint.AutomationId ?? string.Empty;
            return resolutionsByAutomationId.TryGetValue(key, out var resolution)
                ? resolution
                : SelectorResolution.NotFound("no fake resolution configured");
        }
    }

    private sealed class FakeCaptureProvider(Dictionary<string, CaptureResult> resultsByFieldId) : ICaptureProvider
    {
        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken) =>
            Task.FromResult(resultsByFieldId.TryGetValue(field.Id, out var result)
                ? result
                : new CaptureResult(CaptureOutcome.ElementUnavailable, null, "fake", 0, TimeSpan.Zero, []));
    }

    private sealed class UnusedCaptureProvider : ICaptureProvider
    {
        public Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A field that did not resolve must never be captured.");
    }

    private sealed class FakeResolvedElementHandle : ResolvedElementHandle
    {
        public static readonly FakeResolvedElementHandle Instance = new();
    }

    /// <summary>
    /// Yields exactly one signal per configured "FireOnce" call for the given trigger ID,
    /// and otherwise never yields - letting IntegrationTestRunner's own wait-timeout budget
    /// elapse, which is what proves the TimedOut path independent of any real clock.
    /// </summary>
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
