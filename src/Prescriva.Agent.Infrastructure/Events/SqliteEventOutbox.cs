using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Application.Security;
using Prescriva.Agent.Domain.Events;

namespace Prescriva.Agent.Infrastructure.Events;

/// <summary>
/// SQLite-backed <see cref="IEventOutbox"/>. Searchable metadata (event id, session id, sequence,
/// timestamp, type, configuration id/version/revision) is stored in plain columns; only the serialized
/// <see cref="DomainEventPayload"/> — the part that may contain captured field values — is encrypted
/// via <see cref="IPayloadProtector"/> before being written as ciphertext.
/// </summary>
public sealed class SqliteEventOutbox : IEventOutbox
{
    private const string StatusPending = "pending";
    private const string StatusConfirmed = "confirmed";
    private const string StatusQuarantined = "quarantined";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new();

    private readonly string _connectionString;
    private readonly IPayloadProtector _protector;

    public SqliteEventOutbox(string databasePath, IPayloadProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(protector);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // A short busy timeout: under real writer contention, fail fast (SqliteException) rather
        // than silently blocking the caller for SQLite's much longer default retry window.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, DefaultTimeout = 2 }.ToString();
        _protector = protector;

        using var connection = OpenConnection();
        OutboxSchema.EnsureCreated(connection);
    }

    public async Task AppendAsync(DomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var payloadJson = JsonSerializer.Serialize(domainEvent.Payload, PayloadJsonOptions);
        var ciphertext = _protector.Protect(Encoding.UTF8.GetBytes(payloadJson));

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO events
                    (id, configuration_id, configuration_version, configuration_revision, session_id, sequence, timestamp, type, payload_ciphertext, status)
                VALUES
                    ($id, $configurationId, $configurationVersion, $configurationRevision, $sessionId, $sequence, $timestamp, $type, $payload, $status);
                """;
            command.Parameters.AddWithValue("$id", domainEvent.Id.ToString());
            command.Parameters.AddWithValue("$configurationId", domainEvent.ConfigurationId);
            command.Parameters.AddWithValue("$configurationVersion", domainEvent.ConfigurationVersion);
            command.Parameters.AddWithValue("$configurationRevision", (object?)domainEvent.ConfigurationRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$sessionId", domainEvent.SessionId.ToString());
            command.Parameters.AddWithValue("$sequence", domainEvent.Sequence);
            command.Parameters.AddWithValue("$timestamp", domainEvent.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$type", domainEvent.Type);
            command.Parameters.AddWithValue("$payload", ciphertext);
            command.Parameters.AddWithValue("$status", StatusPending);

            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public Task<IReadOnlyList<DomainEvent>> ReadPendingAsync(CancellationToken cancellationToken) =>
        ReadAsync([StatusPending], cancellationToken);

    /// <summary>The pending and the delivered events still kept, in append order; never quarantined ones.</summary>
    public Task<IReadOnlyList<DomainEvent>> ReadExportableAsync(CancellationToken cancellationToken) =>
        ReadAsync([StatusPending, StatusConfirmed], cancellationToken);

    private async Task<IReadOnlyList<DomainEvent>> ReadAsync(string[] statuses, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();

        var rows = new List<(long RowId, string Id, string ConfigurationId, int ConfigurationVersion, string? ConfigurationRevision, string SessionId, long Sequence, string Timestamp, string Type, byte[] Ciphertext)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT rowid, id, configuration_id, configuration_version, configuration_revision, session_id, sequence, timestamp, type, payload_ciphertext
                FROM events
                WHERE status IN ($first, $second)
                ORDER BY rowid ASC;
                """;
            select.Parameters.AddWithValue("$first", statuses[0]);
            select.Parameters.AddWithValue("$second", statuses[^1]);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    (byte[])reader[9]));
            }
        }

        var results = new List<DomainEvent>(rows.Count);
        foreach (var row in rows)
        {
            DomainEventPayload payload;
            try
            {
                var plaintext = _protector.Unprotect(row.Ciphertext);
                payload = JsonSerializer.Deserialize<DomainEventPayload>(Encoding.UTF8.GetString(plaintext), PayloadJsonOptions)
                          ?? throw new InvalidOperationException("Decrypted payload deserialized to null.");
            }
            catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException or JsonException or InvalidOperationException)
            {
                await QuarantineRowAsync(connection, row.RowId, "Payload could not be decrypted or deserialized.", cancellationToken);
                continue;
            }

            results.Add(new DomainEvent(
                Guid.Parse(row.Id),
                row.ConfigurationId,
                row.ConfigurationVersion,
                Guid.Parse(row.SessionId),
                row.Sequence,
                DateTimeOffset.Parse(row.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                row.Type,
                payload,
                row.ConfigurationRevision));
        }

        return results;
    }

    /// <summary>
    /// Every attachment reference held by a pending or confirmed event (as a field value or
    /// inside a confirmed item). A payload that cannot be decrypted is skipped here - it
    /// cannot be shown or delivered either.
    /// </summary>
    public async Task<IReadOnlySet<string>> ReadReferencedAttachmentsAsync(CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT payload_ciphertext FROM events WHERE status IN ($pending, $confirmed);";
        select.Parameters.AddWithValue("$pending", StatusPending);
        select.Parameters.AddWithValue("$confirmed", StatusConfirmed);

        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            DomainEventPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<DomainEventPayload>(Encoding.UTF8.GetString(_protector.Unprotect((byte[])reader[0])), PayloadJsonOptions);
            }
            catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException or JsonException)
            {
                continue;
            }

            if (payload is null) continue;
            var values = payload.Fields.Values.Concat(payload.Items.IsDefault ? [] : payload.Items.SelectMany(item => item.Values));
            foreach (var value in values.Where(Application.Capture.AttachmentReference.IsReference))
            {
                references.Add(value);
            }
        }

        return references;
    }

    public async Task MarkConfirmedAsync(Guid eventId, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE events
            SET status = $status, confirmed_at = $confirmedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", StatusConfirmed);
        command.Parameters.AddWithValue("$confirmedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", eventId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task QuarantineAsync(Guid eventId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE events
            SET status = $status, quarantine_reason = $reason
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", StatusQuarantined);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$id", eventId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteConfirmedBeforeAsync(DateTimeOffset thresholdUtc, CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM events
            WHERE status = $status AND timestamp < $threshold;
            """;
        command.Parameters.AddWithValue("$status", StatusConfirmed);
        command.Parameters.AddWithValue("$threshold", thresholdUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The operator's explicit "clear local data": deletes every event whatever its status
    /// (pending ones included - this is a deliberate, confirmed user action, never an
    /// automatic policy) and compacts the file so freed pages no longer hold the deleted
    /// ciphertext. Returns how many events were deleted.
    /// </summary>
    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        int deleted;
        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM events;";
            deleted = await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken);
        }

        return deleted;
    }

    private static async Task QuarantineRowAsync(SqliteConnection connection, long rowId, string reason, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE events
            SET status = $status, quarantine_reason = $reason
            WHERE rowid = $rowId;
            """;
        command.Parameters.AddWithValue("$status", StatusQuarantined);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$rowId", rowId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
