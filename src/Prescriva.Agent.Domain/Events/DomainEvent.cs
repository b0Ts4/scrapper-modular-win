using System.Collections.Immutable;

namespace Prescriva.Agent.Domain.Events;

public sealed record DomainEventPayload(
    ImmutableDictionary<string, string> Fields,
    ImmutableArray<ImmutableDictionary<string, string>> Items);

/// <summary>
/// One business event. <see cref="ConfigurationVersion"/> is the configuration's schema version;
/// <see cref="ConfigurationRevision"/> identifies the exact configuration content that produced
/// the event (the same content hash an approval is bound to), or null when not known.
/// </summary>
public sealed record DomainEvent(
    Guid Id,
    string ConfigurationId,
    int ConfigurationVersion,
    Guid SessionId,
    long Sequence,
    DateTimeOffset Timestamp,
    string Type,
    DomainEventPayload Payload,
    string? ConfigurationRevision = null);
