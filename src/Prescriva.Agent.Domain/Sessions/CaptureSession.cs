using System.Collections.Immutable;

namespace Prescriva.Agent.Domain.Sessions;

public sealed record CaptureSession(
    Guid Id,
    string CurrentStageId,
    SessionState State,
    long LastSequence,
    ImmutableDictionary<string, string> Values,
    ImmutableArray<ImmutableDictionary<string, string>> ConfirmedItems,
    ImmutableHashSet<Guid> UsedEventIds)
{
    public static CaptureSession Start(Guid id, string stageId) => new(
        id, stageId, SessionState.Active, 0,
        ImmutableDictionary<string, string>.Empty, [], ImmutableHashSet<Guid>.Empty);
}
