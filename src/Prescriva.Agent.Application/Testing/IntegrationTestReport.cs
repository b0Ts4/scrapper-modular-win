using System.Collections.Immutable;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Testing;

/// <summary>The outcome of resolving (and, if found, capturing) one configured field during a test run.</summary>
public enum FieldCheckOutcome
{
    /// <summary>Exactly one candidate matched with a clear lead; a value was read from it.</summary>
    Found,

    /// <summary>The best-scoring candidate did not have a clear enough lead; resolution is not safe to act on.</summary>
    Ambiguous,

    /// <summary>No candidate scored high enough, the target window was missing, or resolution timed out.</summary>
    NotFound,
}

/// <summary>
/// One field's result from a test run. <see cref="ProviderId"/>, <see cref="Confidence"/>
/// and <see cref="Value"/> are only ever populated for <see cref="FieldCheckOutcome.Found"/> -
/// every other outcome carries <see cref="FailureCode"/> instead, a stable machine code
/// (never free text derived from a captured value) that <c>TestModeViewModel</c> maps to
/// actionable Portuguese text for display.
///
/// <see cref="Value"/> exists purely so a reviewer running the test can see that a real
/// value was actually read back, not just that resolution succeeded; this report is never
/// passed to <c>IConfigurationStore</c> or <c>IEventOutbox</c>, so it never reaches disk on
/// its own - per this task's "captured test values stay in memory unless explicitly saved"
/// rule, only an explicit save action (outside this type) may persist it.
/// </summary>
public sealed record FieldCheckResult(
    string FieldId,
    FieldCheckOutcome Outcome,
    string? ProviderId,
    double Confidence,
    string? FailureCode,
    string? Value = null)
{
    public const string FieldNotFoundCode = "FIELD_NOT_FOUND";
    public const string FieldAmbiguousCode = "FIELD_AMBIGUOUS";
}

/// <summary>The outcome of waiting for one configured trigger to fire during a test run.</summary>
public enum TriggerCheckOutcome
{
    /// <summary>A signal for this trigger was observed within the test run's wait window.</summary>
    Detected,

    /// <summary>No signal for this trigger was observed before the wait window elapsed.</summary>
    TimedOut,
}

/// <summary>
/// One trigger's result from a test run, including the stage transitions and event types
/// its configured actions declare - listed here (not simulated through the full session
/// engine, which is <see cref="Runtime.SessionCoordinator"/>'s job, not this test runner's)
/// so a reviewer can see exactly what firing this trigger is configured to do, alongside
/// whether it was actually observed to fire at all.
/// </summary>
public sealed record TriggerCheckResult(
    string TriggerId,
    TriggerCheckOutcome Outcome,
    ImmutableArray<string> StageTransitions,
    ImmutableArray<string> EmittedEventTypes,
    string? FailureCode)
{
    public const string TriggerTimedOutCode = "TRIGGER_TIMED_OUT";
}

/// <summary>
/// The full result of running <see cref="IntegrationTestRunner"/> once against a
/// configuration: one <see cref="FieldCheckResult"/> per configured field and one
/// <see cref="TriggerCheckResult"/> per configured trigger, plus the exact content
/// <see cref="Fingerprint"/> (via <see cref="ConfigurationFingerprint"/>) the configuration
/// had at the moment this run happened - the same fingerprint an approval created from a
/// passing report is bound to.
/// </summary>
public sealed record IntegrationTestReport(
    string ConfigurationId,
    string Fingerprint,
    DateTimeOffset RanAtUtc,
    ImmutableArray<FieldCheckResult> FieldResults,
    ImmutableArray<TriggerCheckResult> TriggerResults)
{
    /// <summary>True only when every field resolved (Found) and every trigger fired (Detected).</summary>
    public bool AllPassed =>
        FieldResults.All(result => result.Outcome == FieldCheckOutcome.Found) &&
        TriggerResults.All(trigger => trigger.Outcome == TriggerCheckOutcome.Detected);

    /// <summary>
    /// Produces the <see cref="ConfigurationApproval"/> this report earns, or null when
    /// <see cref="AllPassed"/> is false - a failed run can never approve a configuration,
    /// by construction rather than by a caller remembering to check first.
    /// </summary>
    public ConfigurationApproval? ToApproval(DateTimeOffset approvedAtUtc) =>
        AllPassed ? new ConfigurationApproval(ConfigurationId, Fingerprint, approvedAtUtc) : null;
}
