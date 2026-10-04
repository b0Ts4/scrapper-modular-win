using Prescriva.Agent.Domain.Sessions;

namespace Prescriva.Agent.Application.Runtime;

public enum RuntimeDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum RuntimeDiagnosticCode
{
    /// <summary>A domain event was applied by <see cref="Domain.Sessions.SessionEngine"/> and durably appended to the outbox.</summary>
    EventPersisted,

    /// <summary>A trigger occurrence was rejected by the session engine; no event was persisted.</summary>
    SessionRejected,

    /// <summary>A trigger fired for a stage the session is not currently in; it was ignored.</summary>
    SessionIgnored,

    /// <summary>
    /// Selector resolution for a field did not return a clean, confident match (ambiguous,
    /// not found, the window went missing, or it timed out). The field is treated as absent
    /// for this occurrence; whether that blocks the transition depends on whether the field
    /// is required.
    /// </summary>
    SelectorFallback,

    /// <summary>The application instance behind this session closed; the session was ended.</summary>
    SessionClosed,

    /// <summary>A configured trigger's native subscription is live; the session is monitoring it.</summary>
    TriggerWatchStarted,

    /// <summary>
    /// A configured trigger could not be watched (e.g. its element could not be resolved,
    /// or disappeared mid-watch). Reported once per failure streak; the watch is retried
    /// until it recovers (announced by a new <see cref="TriggerWatchStarted"/>) or the
    /// session ends. Occurrences while it is failing are not observed; the session's other
    /// triggers keep running.
    /// </summary>
    TriggerWatchFailed
}

/// <summary>
/// An observable diagnostic raised by <see cref="SessionCoordinator"/>/<see cref="AgentRuntime"/>.
/// Deliberately plain data: codes, severity, optional selector confidence, optional
/// provider name and elapsed time - never a captured field value.
/// </summary>
public sealed record RuntimeDiagnostic(
    RuntimeDiagnosticCode Code,
    RuntimeDiagnosticSeverity Severity,
    Guid SessionId,
    DateTimeOffset Timestamp,
    TimeSpan Elapsed,
    string? TriggerId = null,
    string? FieldId = null,
    string? EventType = null,
    SessionFailureCode? FailureCode = null,
    double? Confidence = null,
    string? Provider = null);
