using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Domain.Sessions;

namespace Prescriva.Agent.Application.Tests.Runtime;

/// <summary>
/// Spec §8: later changes in the target application can put an integration in a Degraded
/// or Broken state, always with a visible reason. The tracker derives that state from the
/// runtime's own diagnostics and recovers when the problem stops.
/// </summary>
public sealed class IntegrationHealthTrackerTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Starts_healthy()
    {
        var health = new IntegrationHealthTracker(Configuration()).Current;

        Assert.Equal(IntegrationHealthState.Healthy, health.State);
        Assert.Empty(health.Issues);
    }

    [Fact]
    public void A_low_confidence_match_degrades_the_integration_naming_the_field_until_a_clean_capture()
    {
        var tracker = new IntegrationHealthTracker(Configuration());

        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.SelectorFallback, fieldId: "notes", confidence: 0.6));
        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.EventPersisted, triggerId: "add_item", eventType: "item_added"));

        var degraded = tracker.Current;
        Assert.Equal(IntegrationHealthState.Degraded, degraded.State);
        var issue = Assert.Single(degraded.Issues);
        Assert.Equal(IntegrationIssueKind.LowConfidenceMatch, issue.Kind);
        Assert.Equal("notes", issue.FieldId);

        // The next occurrence captures the field cleanly: healthy again.
        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.EventPersisted, triggerId: "add_item", eventType: "item_added"));
        Assert.Equal(IntegrationHealthState.Healthy, tracker.Current.State);
    }

    [Fact]
    public void A_trigger_that_cannot_be_watched_breaks_the_integration_until_it_is_watched_again()
    {
        var tracker = new IntegrationHealthTracker(Configuration());

        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.TriggerWatchFailed, triggerId: "finish_budget"));
        var broken = tracker.Current;
        Assert.Equal(IntegrationHealthState.Broken, broken.State);
        Assert.Equal("finish_budget", Assert.Single(broken.Issues).TriggerId);

        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.TriggerWatchStarted, triggerId: "finish_budget"));
        Assert.Equal(IntegrationHealthState.Healthy, tracker.Current.State);
    }

    [Fact]
    public void A_field_that_cannot_be_captured_breaks_the_integration_and_outranks_degradation()
    {
        var tracker = new IntegrationHealthTracker(Configuration());

        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.SelectorFallback, fieldId: "notes", confidence: 0.6));
        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.SessionRejected, triggerId: "add_item", fieldId: "medication", failureCode: SessionFailureCode.CaptureFailed));

        var health = tracker.Current;
        Assert.Equal(IntegrationHealthState.Broken, health.State);
        Assert.Equal(IntegrationIssueKind.FieldUnreadable, health.Issues[0].Kind); // most severe first
        Assert.Equal("medication", health.Issues[0].FieldId);
        Assert.Contains(health.Issues, issue => issue.Kind == IntegrationIssueKind.LowConfidenceMatch);

        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.EventPersisted, triggerId: "add_item", eventType: "item_added"));
        Assert.Equal(IntegrationHealthState.Healthy, tracker.Current.State);
    }

    [Fact]
    public void An_operator_leaving_a_required_field_empty_is_not_a_configuration_problem()
    {
        var tracker = new IntegrationHealthTracker(Configuration());

        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.SessionRejected, triggerId: "add_item", fieldId: "medication", failureCode: SessionFailureCode.MissingRequiredField));

        Assert.Equal(IntegrationHealthState.Healthy, tracker.Current.State);
    }

    [Fact]
    public void A_persisted_event_only_clears_fields_its_own_trigger_captures()
    {
        var tracker = new IntegrationHealthTracker(Configuration());

        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.SessionRejected, triggerId: "add_item", fieldId: "medication", failureCode: SessionFailureCode.CaptureFailed));
        tracker.Apply(Diagnostic(RuntimeDiagnosticCode.EventPersisted, triggerId: "finish_budget", eventType: "budget_finished"));

        Assert.Equal(IntegrationHealthState.Broken, tracker.Current.State);
    }

    private static RuntimeDiagnostic Diagnostic(
        RuntimeDiagnosticCode code,
        string? triggerId = null,
        string? fieldId = null,
        string? eventType = null,
        SessionFailureCode? failureCode = null,
        double? confidence = null) =>
        new(code, code is RuntimeDiagnosticCode.TriggerWatchFailed or RuntimeDiagnosticCode.SessionRejected ? RuntimeDiagnosticSeverity.Error : RuntimeDiagnosticSeverity.Info,
            Session, Now, TimeSpan.Zero, triggerId, fieldId, eventType, failureCode, confidence);

    private static IntegrationConfiguration Configuration() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "health-config",
        "Health configuration",
        new ApplicationDefinition("Fake.App", "Fake Window"),
        [
            new FieldDefinition("medication", "budget", "Medicamento", Required: true, Selector: new ElementFingerprint("Fake.App", "Fake Window", AutomationId: "Medication")),
            new FieldDefinition("notes", "budget", "Observações", Required: false, Selector: new ElementFingerprint("Fake.App", "Fake Window", AutomationId: "Notes")),
        ],
        [new StageDefinition("budget", "Orçamento")],
        [
            new TriggerDefinition("add_item", "budget", new ElementFingerprint("Fake.App", "Fake Window", AutomationId: "Add"), "Invoke",
                [new CaptureFieldsAction(["medication", "notes"]), new EmitEventAction("item_added")]),
            new TriggerDefinition("finish_budget", "budget", new ElementFingerprint("Fake.App", "Fake Window", AutomationId: "Finish"), "Invoke",
                [new FinishSessionAction()]),
        ]);
}
