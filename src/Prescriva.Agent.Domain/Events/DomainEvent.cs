using System.Collections.Immutable;

namespace Prescriva.Agent.Domain.Events;

public sealed record DomainEventPayload(
    ImmutableDictionary<string, string> Fields,
    ImmutableArray<ImmutableDictionary<string, string>> Items);

public sealed record DomainEvent(
    Guid Id,
    string ConfigurationId,
    int ConfigurationVersion,
    Guid SessionId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Type,
    DomainEventPayload Payload);
