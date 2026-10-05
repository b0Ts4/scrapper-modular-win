using Prescriva.Agent.Application.Capture;

namespace Prescriva.Agent.Infrastructure.Events;

/// <summary>Keeps attachments tied to the lifetime of the events that reference them.</summary>
public static class LocalDataMaintenance
{
    /// <summary>
    /// Deletes every stored attachment no pending or confirmed event references any more
    /// (after retention removed old confirmed events, or a capture was rejected after its
    /// attachment was stored). Returns how many were deleted.
    /// </summary>
    public static async Task<int> CollectAttachmentGarbageAsync(
        SqliteEventOutbox outbox,
        IAttachmentStore attachments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(attachments);

        var referenced = await outbox.ReadReferencedAttachmentsAsync(cancellationToken).ConfigureAwait(false);
        return await attachments.DeleteUnreferencedAsync(referenced.ToArray(), cancellationToken).ConfigureAwait(false);
    }
}
