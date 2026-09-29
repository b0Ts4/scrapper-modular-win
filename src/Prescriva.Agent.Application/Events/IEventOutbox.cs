using Prescriva.Agent.Domain.Events;

namespace Prescriva.Agent.Application.Events;

public interface IEventOutbox
{
    /// <summary>
    /// Persists a domain event durably and atomically. Appending an event whose <see cref="DomainEvent.Id"/>
    /// already exists is idempotent: it does not create a duplicate row and does not throw.
    /// </summary>
    Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every event that is still pending confirmation, in the order they were appended.
    /// An event whose stored payload cannot be decrypted is moved to quarantine as a side effect
    /// and is excluded from the returned list rather than raised as an exception.
    /// </summary>
    Task<IReadOnlyList<DomainEvent>> ReadPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Marks a pending event as confirmed (successfully delivered/processed).
    /// </summary>
    Task MarkConfirmedAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>
    /// Moves an event to quarantine, recording a reason. Quarantine never includes plaintext payload data.
    /// </summary>
    Task QuarantineAsync(Guid eventId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes confirmed events whose original timestamp is strictly before <paramref name="thresholdUtc"/>.
    /// Pending or quarantined events are never affected by retention.
    /// </summary>
    Task DeleteConfirmedBeforeAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken);
}
