using System.Collections.Immutable;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Infrastructure.Events;

namespace Prescriva.Agent.Infrastructure.Tests.Events;

public sealed class RetentionServiceTests
{
    private sealed class RecordingOutbox : IEventOutbox
    {
        public DateTimeOffset? CapturedThreshold { get; private set; }

        public Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<DomainEvent>> ReadPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DomainEvent>>([]);

        public Task MarkConfirmedAsync(Guid eventId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task QuarantineAsync(Guid eventId, string reason, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteConfirmedBeforeAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken)
        {
            CapturedThreshold = thresholdUtc;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Apply_deletes_using_a_threshold_exactly_seven_days_before_now()
    {
        var fixedNow = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var outbox = new RecordingOutbox();
        var service = new RetentionService(outbox, () => fixedNow);

        await service.ApplyAsync(CancellationToken.None);

        Assert.Equal(fixedNow - TimeSpan.FromDays(7), outbox.CapturedThreshold);
    }

    [Fact]
    public async Task End_to_end_retention_deletes_only_confirmed_events_strictly_older_than_seven_days()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prescriva-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "outbox.db");
        try
        {
            var outbox = new Prescriva.Agent.Infrastructure.Events.SqliteEventOutbox(
                dbPath, new Prescriva.Agent.Infrastructure.Security.DpapiPayloadProtector());

            var now = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
            var exactlySevenDaysId = Guid.NewGuid();
            var eightDaysId = Guid.NewGuid();

            DomainEvent MakeEvent(Guid id, DateTimeOffset timestamp) => new(
                id, "budget-flow", 1, Guid.NewGuid(), 1, timestamp, "item_captured",
                new DomainEventPayload(ImmutableDictionary<string, string>.Empty, ImmutableArray<ImmutableDictionary<string, string>>.Empty));

            await outbox.AppendAsync(MakeEvent(exactlySevenDaysId, now - TimeSpan.FromDays(7)), CancellationToken.None);
            await outbox.AppendAsync(MakeEvent(eightDaysId, now - TimeSpan.FromDays(8)), CancellationToken.None);
            await outbox.MarkConfirmedAsync(exactlySevenDaysId, CancellationToken.None);
            await outbox.MarkConfirmedAsync(eightDaysId, CancellationToken.None);

            var service = new RetentionService(outbox, () => now);
            await service.ApplyAsync(CancellationToken.None);

            var remaining = await ReadAllIdsAsync(dbPath);
            Assert.Contains(exactlySevenDaysId.ToString(), remaining);
            Assert.DoesNotContain(eightDaysId.ToString(), remaining);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<List<string>> ReadAllIdsAsync(string dbPath)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM events;";
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return ids;
    }
}
