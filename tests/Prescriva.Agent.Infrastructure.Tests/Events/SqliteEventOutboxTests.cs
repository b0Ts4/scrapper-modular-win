using System.Collections.Immutable;
using System.Text;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Application.Security;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;

namespace Prescriva.Agent.Infrastructure.Tests.Events;

public sealed class SqliteEventOutboxTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-outbox-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public SqliteEventOutboxTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "outbox.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static DomainEvent MakeEvent(Guid sessionId, long sequence, string secretValue, Guid? id = null) =>
        new(
            id ?? Guid.NewGuid(),
            "budget-flow",
            1,
            sessionId,
            sequence,
            DateTimeOffset.UtcNow,
            "item_captured",
            new DomainEventPayload(
                ImmutableDictionary<string, string>.Empty.Add("patient_name", secretValue),
                ImmutableArray<ImmutableDictionary<string, string>>.Empty));

    [Fact]
    public async Task Plaintext_payload_never_appears_in_the_database_file_bytes()
    {
        const string secret = "SEGREDO-PACIENTE-b7e21f9c";
        var outbox = new SqliteEventOutbox(_dbPath, new DpapiPayloadProtector());
        var domainEvent = MakeEvent(Guid.NewGuid(), 1, secret);

        await outbox.AppendAsync(domainEvent, CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var bytes = await File.ReadAllBytesAsync(_dbPath);
        var fileText = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(secret, fileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Round_trips_a_pending_event_through_append_and_read()
    {
        const string secret = "valor-capturado-42.50";
        var outbox = new SqliteEventOutbox(_dbPath, new DpapiPayloadProtector());
        var sessionId = Guid.NewGuid();
        var domainEvent = MakeEvent(sessionId, 1, secret);

        await outbox.AppendAsync(domainEvent, CancellationToken.None);
        var pending = await outbox.ReadPendingAsync(CancellationToken.None);

        var read = Assert.Single(pending);
        Assert.Equal(domainEvent.Id, read.Id);
        Assert.Equal(domainEvent.ConfigurationId, read.ConfigurationId);
        Assert.Equal(domainEvent.ConfigurationVersion, read.ConfigurationVersion);
        Assert.Equal(domainEvent.SessionId, read.SessionId);
        Assert.Equal(domainEvent.Sequence, read.Sequence);
        Assert.Equal(domainEvent.Type, read.Type);
        Assert.Equal(secret, read.Payload.Fields["patient_name"]);
    }

    [Fact]
    public async Task Appending_the_same_event_id_twice_is_idempotent()
    {
        var outbox = new SqliteEventOutbox(_dbPath, new DpapiPayloadProtector());
        var eventId = Guid.NewGuid();
        var domainEvent = MakeEvent(Guid.NewGuid(), 1, "valor", eventId);

        await outbox.AppendAsync(domainEvent, CancellationToken.None);
        await outbox.AppendAsync(domainEvent, CancellationToken.None);

        var pending = await outbox.ReadPendingAsync(CancellationToken.None);
        Assert.Single(pending);
    }

    [Fact]
    public async Task Restart_preserves_insertion_order_across_sessions()
    {
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var idOne = Guid.NewGuid();
        var idTwo = Guid.NewGuid();
        var idThree = Guid.NewGuid();

        var outboxBeforeRestart = new SqliteEventOutbox(_dbPath, new DpapiPayloadProtector());
        await outboxBeforeRestart.AppendAsync(MakeEvent(sessionA, 1, "v1", idOne), CancellationToken.None);
        await outboxBeforeRestart.AppendAsync(MakeEvent(sessionB, 1, "v2", idTwo), CancellationToken.None);
        await outboxBeforeRestart.AppendAsync(MakeEvent(sessionA, 2, "v3", idThree), CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var outboxAfterRestart = new SqliteEventOutbox(_dbPath, new DpapiPayloadProtector());
        var pending = await outboxAfterRestart.ReadPendingAsync(CancellationToken.None);

        Assert.Equal([idOne, idTwo, idThree], pending.Select(e => e.Id));
    }

    [Fact]
    public async Task Payload_that_cannot_be_decrypted_is_quarantined_instead_of_crashing_or_disappearing()
    {
        var outbox = new SqliteEventOutbox(_dbPath, new DpapiPayloadProtector());
        var eventId = Guid.NewGuid();
        await outbox.AppendAsync(MakeEvent(Guid.NewGuid(), 1, "valor", eventId), CancellationToken.None);

        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE events SET payload_ciphertext = $corrupt WHERE id = $id;";
            command.Parameters.AddWithValue("$corrupt", new byte[] { 9, 9, 9, 9 });
            command.Parameters.AddWithValue("$id", eventId.ToString());
            await command.ExecuteNonQueryAsync();
        }

        var pending = await outbox.ReadPendingAsync(CancellationToken.None);
        Assert.Empty(pending);

        await using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await verifyConnection.OpenAsync();
        await using var verifyCommand = verifyConnection.CreateCommand();
        verifyCommand.CommandText = "SELECT status, quarantine_reason FROM events WHERE id = $id;";
        verifyCommand.Parameters.AddWithValue("$id", eventId.ToString());
        await using var reader = await verifyCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("quarantined", reader.GetString(0));
        Assert.False(reader.IsDBNull(1));
    }

    [Fact]
    public async Task A_failure_that_happens_after_the_insert_succeeds_but_before_commit_leaves_no_partial_row()
    {
        // Force the underlying SQLite COMMIT itself to fail, so the insert genuinely happens
        // (ExecuteNonQueryAsync completes against the transaction) but the transaction can never
        // become durable. This exercises the real `catch { transaction.Rollback(); throw; }` path
        // in SqliteEventOutbox.AppendAsync: a second connection holds an open read transaction
        // (a SHARED lock) on the same database file for the whole test. SQLite's rollback-journal
        // locking model allows our outbox's own write transaction to acquire a RESERVED lock and
        // execute the INSERT while a SHARED lock is held elsewhere, but COMMIT must escalate to an
        // EXCLUSIVE lock to flush pages back into the main database file — and that escalation is
        // what the competing SHARED lock blocks, so Commit() itself throws.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        var outbox = new SqliteEventOutbox(_dbPath, new DpapiPayloadProtector());
        var eventId = Guid.NewGuid();

        await using var blockingConnection = new SqliteConnection(connectionString);
        await blockingConnection.OpenAsync();
        await using var blockingTransaction = (SqliteTransaction)await blockingConnection.BeginTransactionAsync();
        await using (var readCommand = blockingConnection.CreateCommand())
        {
            readCommand.Transaction = blockingTransaction;
            readCommand.CommandText = "SELECT COUNT(*) FROM events;";
            await readCommand.ExecuteScalarAsync();
        }

        try
        {
            await Assert.ThrowsAnyAsync<SqliteException>(() =>
                outbox.AppendAsync(MakeEvent(Guid.NewGuid(), 1, "valor", eventId), CancellationToken.None));
        }
        finally
        {
            await blockingTransaction.RollbackAsync();
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events WHERE id = $id;";
        command.Parameters.AddWithValue("$id", eventId.ToString());
        var count = (long)(await command.ExecuteScalarAsync())!;
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Marking_confirmed_then_deleting_before_a_threshold_removes_only_confirmed_events()
    {
        var outbox = new SqliteEventOutbox(_dbPath, new DpapiPayloadProtector());
        var oldConfirmedId = Guid.NewGuid();
        var recentConfirmedId = Guid.NewGuid();
        var oldPendingId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        var oldEvent = MakeEvent(Guid.NewGuid(), 1, "v1", oldConfirmedId) with { Timestamp = now - TimeSpan.FromDays(10) };
        var recentEvent = MakeEvent(Guid.NewGuid(), 1, "v2", recentConfirmedId) with { Timestamp = now - TimeSpan.FromDays(1) };
        var oldPendingEvent = MakeEvent(Guid.NewGuid(), 1, "v3", oldPendingId) with { Timestamp = now - TimeSpan.FromDays(10) };

        await outbox.AppendAsync(oldEvent, CancellationToken.None);
        await outbox.AppendAsync(recentEvent, CancellationToken.None);
        await outbox.AppendAsync(oldPendingEvent, CancellationToken.None);

        await outbox.MarkConfirmedAsync(oldConfirmedId, CancellationToken.None);
        await outbox.MarkConfirmedAsync(recentConfirmedId, CancellationToken.None);

        await outbox.DeleteConfirmedBeforeAsync(now - TimeSpan.FromDays(7), CancellationToken.None);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM events;";
        await using var reader = await command.ExecuteReaderAsync();
        var remainingIds = new List<string>();
        while (await reader.ReadAsync()) remainingIds.Add(reader.GetString(0));

        Assert.DoesNotContain(oldConfirmedId.ToString(), remainingIds);
        Assert.Contains(recentConfirmedId.ToString(), remainingIds);
        Assert.Contains(oldPendingId.ToString(), remainingIds);
    }
}
