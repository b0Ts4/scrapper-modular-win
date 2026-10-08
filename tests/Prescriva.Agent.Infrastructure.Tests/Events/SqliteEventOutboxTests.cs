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

    [Fact]
    public async Task DeleteAllAsync_removes_pending_confirmed_and_quarantined_events_and_their_ciphertext_from_the_file()
    {
        var protector = new DpapiPayloadProtector();
        var outbox = new SqliteEventOutbox(_dbPath, protector);
        var sessionId = Guid.NewGuid();
        var confirmedId = Guid.NewGuid();
        var quarantinedId = Guid.NewGuid();
        var events = new[]
        {
            MakeEvent(sessionId, 1, "pendente-limpeza-1"),
            MakeEvent(sessionId, 2, "confirmado-limpeza-2", confirmedId),
            MakeEvent(sessionId, 3, "quarentena-limpeza-3", quarantinedId),
        };
        foreach (var domainEvent in events)
        {
            await outbox.AppendAsync(domainEvent, CancellationToken.None);
        }

        await outbox.MarkConfirmedAsync(confirmedId, CancellationToken.None);
        await outbox.QuarantineAsync(quarantinedId, "test", CancellationToken.None);
        SqliteConnection.ClearAllPools();
        var ciphertextBefore = await ReadCiphertextsAsync();
        Assert.Equal(3, ciphertextBefore.Count);

        var deleted = await outbox.DeleteAllAsync(CancellationToken.None);

        Assert.Equal(3, deleted);
        Assert.Empty(await outbox.ReadPendingAsync(CancellationToken.None));
        SqliteConnection.ClearAllPools();
        var fileBytes = await File.ReadAllBytesAsync(_dbPath);
        foreach (var ciphertext in ciphertextBefore)
        {
            // Not even the encrypted payload may linger in freed database pages.
            Assert.True(fileBytes.AsSpan().IndexOf(ciphertext.AsSpan(0, 32)) < 0, "Deleted ciphertext is still present in the database file.");
        }

        // The outbox keeps working after a cleanup.
        await outbox.AppendAsync(MakeEvent(Guid.NewGuid(), 1, "depois"), CancellationToken.None);
        Assert.Single(await outbox.ReadPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_configuration_content_revision_round_trips()
    {
        var outbox = new SqliteEventOutbox(_dbPath, new DpapiPayloadProtector());
        var domainEvent = MakeEvent(Guid.NewGuid(), 1, "valor") with { ConfigurationRevision = "746DDFCDB43694B0" };

        await outbox.AppendAsync(domainEvent, CancellationToken.None);

        Assert.Equal("746DDFCDB43694B0", Assert.Single(await outbox.ReadPendingAsync(CancellationToken.None)).ConfigurationRevision);
    }

    [Fact]
    public async Task A_version_1_queue_is_migrated_in_place_keeping_its_events()
    {
        var protector = new DpapiPayloadProtector();
        var existingId = Guid.NewGuid();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE schema_version (version INTEGER NOT NULL);
                INSERT INTO schema_version (version) VALUES (1);
                CREATE TABLE events (
                    id TEXT PRIMARY KEY,
                    configuration_id TEXT NOT NULL,
                    configuration_version INTEGER NOT NULL,
                    session_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    timestamp TEXT NOT NULL,
                    type TEXT NOT NULL,
                    payload_ciphertext BLOB NOT NULL,
                    status TEXT NOT NULL,
                    quarantine_reason TEXT NULL,
                    confirmed_at TEXT NULL,
                    UNIQUE (session_id, sequence)
                );
                INSERT INTO events (id, configuration_id, configuration_version, session_id, sequence, timestamp, type, payload_ciphertext, status)
                VALUES ($id, 'budget-flow', 1, $session, 1, '2026-10-01T10:00:00.0000000+00:00', 'item_added', $payload, 'pending');
                """;
            command.Parameters.AddWithValue("$id", existingId.ToString("D"));
            command.Parameters.AddWithValue("$session", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$payload", protector.Protect(Encoding.UTF8.GetBytes("""{"Fields":{"medication":"antigo"},"Items":[]}""")));
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var outbox = new SqliteEventOutbox(_dbPath, protector);
        await outbox.AppendAsync(MakeEvent(Guid.NewGuid(), 1, "novo") with { ConfigurationRevision = "REV2" }, CancellationToken.None);
        var pending = await outbox.ReadPendingAsync(CancellationToken.None);

        var existing = Assert.Single(pending, e => e.Id == existingId);
        Assert.Equal("antigo", existing.Payload.Fields["medication"]);
        Assert.Null(existing.ConfigurationRevision);
        Assert.Equal("REV2", Assert.Single(pending, e => e.Id != existingId).ConfigurationRevision);

        SqliteConnection.ClearAllPools();
        await using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await check.OpenAsync();
        await using var version = check.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_version;";
        Assert.Equal(2L, (long)(await version.ExecuteScalarAsync())!);
    }

    private async Task<List<byte[]>> ReadCiphertextsAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_ciphertext FROM events;";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<byte[]>();
        while (await reader.ReadAsync())
        {
            result.Add((byte[])reader[0]);
        }

        return result;
    }

    [Fact]
    public async Task ReadExportableAsync_returns_pending_and_confirmed_events_in_order_but_never_quarantined_ones()
    {
        var outbox = new SqliteEventOutbox(_dbPath, new PassThroughProtector());
        var session = Guid.NewGuid();
        var delivered = MakeEvent(session, 1, "delivered");
        var quarantined = MakeEvent(session, 2, "quarantined");
        var pending = MakeEvent(session, 3, "pending");
        foreach (var domainEvent in new[] { delivered, quarantined, pending })
        {
            await outbox.AppendAsync(domainEvent, CancellationToken.None);
        }

        await outbox.MarkConfirmedAsync(delivered.Id, CancellationToken.None);
        await outbox.QuarantineAsync(quarantined.Id, "test", CancellationToken.None);

        var exported = await outbox.ReadExportableAsync(CancellationToken.None);

        Assert.Equal([delivered.Id, pending.Id], exported.Select(domainEvent => domainEvent.Id));
        Assert.Equal([pending.Id], (await outbox.ReadPendingAsync(CancellationToken.None)).Select(domainEvent => domainEvent.Id));
    }

    /// <summary>No encryption, so the test also runs where DPAPI does not exist.</summary>
    private sealed class PassThroughProtector : IPayloadProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
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
