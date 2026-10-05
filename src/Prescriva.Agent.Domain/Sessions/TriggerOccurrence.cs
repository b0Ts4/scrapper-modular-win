using System.Collections.Immutable;

namespace Prescriva.Agent.Domain.Sessions;

/// <summary>Event IDs are allocated by the caller and consumed in emission order.</summary>
public sealed record TriggerOccurrence(string TriggerId, ImmutableArray<Guid> EventIds);
