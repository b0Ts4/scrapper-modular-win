using System.Collections.Immutable;
using Prescriva.Agent.Domain.Events;

namespace Prescriva.Agent.Domain.Sessions;

public enum SessionTransitionStatus
{
    Applied,
    Ignored,
    Rejected
}

public enum SessionFailureCode
{
    MissingRequiredField,
    CaptureFailed,
    UnknownTrigger,
    SessionEnded,
    MissingEventId,
    InvalidEventId,
    DuplicateEventId,
    SequenceExhausted,
    InvalidSession,
    UnsupportedAction
}

public sealed record SessionFailure(
    SessionFailureCode Code,
    string? FieldId = null,
    CaptureFailure? CaptureFailure = null);

public sealed record SessionTransitionResult(
    CaptureSession Session,
    ImmutableArray<DomainEvent> Events,
    ImmutableArray<SessionFailure> Failures,
    SessionTransitionStatus Status);
