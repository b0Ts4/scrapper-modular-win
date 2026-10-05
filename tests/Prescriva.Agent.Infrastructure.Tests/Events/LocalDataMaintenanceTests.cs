using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Security;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Infrastructure.Events;

namespace Prescriva.Agent.Infrastructure.Tests.Events;

/// <summary>
/// Attachments live only as long as an event (pending or confirmed, as a field or inside a
/// confirmed item) still references them; retention and cleanup take them along.
/// </summary>
public sealed class LocalDataMaintenanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-maintenance-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public LocalDataMaintenanceTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "events.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Garbage_collection_keeps_attachments_referenced_by_pending_or_confirmed_events_and_drops_the_rest()
    {
        var protector = new ReversibleProtector();
        var outbox = new SqliteEventOutbox(_dbPath, protector);
        var attachments = new SqliteAttachmentStore(_dbPath, protector);
        var inField = await attachments.SaveAsync(new CapturedAttachment([1], "a.pdf", "application/pdf", AttachmentSource.File), CancellationToken.None);
        var inItem = await attachments.SaveAsync(new CapturedAttachment([2], "b.pdf", "application/pdf", AttachmentSource.File), CancellationToken.None);
        var confirmedOnly = await attachments.SaveAsync(new CapturedAttachment([3], "c.png", "image/png", AttachmentSource.Screen), CancellationToken.None);
        var orphan = await attachments.SaveAsync(new CapturedAttachment([4], "d.png", "image/png", AttachmentSource.Screen), CancellationToken.None);

        var session = Guid.NewGuid();
        await outbox.AppendAsync(Event(session, 1, fields: new() { ["prescription"] = inField.Reference }), CancellationToken.None);
        await outbox.AppendAsync(Event(session, 2, items: [new Dictionary<string, string> { ["prescription"] = inItem.Reference }.ToImmutableDictionary()]), CancellationToken.None);
        var confirmed = Event(session, 3, fields: new() { ["prescription"] = confirmedOnly.Reference });
        await outbox.AppendAsync(confirmed, CancellationToken.None);
        await outbox.MarkConfirmedAsync(confirmed.Id, CancellationToken.None);

        var deleted = await LocalDataMaintenance.CollectAttachmentGarbageAsync(outbox, attachments, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.NotNull(await attachments.ReadAsync(inField.Reference, CancellationToken.None));
        Assert.NotNull(await attachments.ReadAsync(inItem.Reference, CancellationToken.None));
        Assert.NotNull(await attachments.ReadAsync(confirmedOnly.Reference, CancellationToken.None));
        Assert.Null(await attachments.ReadAsync(orphan.Reference, CancellationToken.None));
    }

    private static DomainEvent Event(
        Guid session,
        long sequence,
        Dictionary<string, string>? fields = null,
        ImmutableDictionary<string, string>[]? items = null) =>
        new(Guid.NewGuid(), "file-config", 1, session, sequence, DateTimeOffset.UtcNow, "item_added",
            new DomainEventPayload(
                (fields ?? new()).ToImmutableDictionary(),
                items is null ? ImmutableArray<ImmutableDictionary<string, string>>.Empty : [.. items]));

    private sealed class ReversibleProtector : IPayloadProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}
