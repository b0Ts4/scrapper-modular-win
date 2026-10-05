using Microsoft.Data.Sqlite;

namespace Prescriva.Agent.Infrastructure.Events;

/// <summary>
/// Creates and migrates the event outbox SQLite schema. Version 1 is the original table; version 2
/// adds the nullable <c>configuration_revision</c> column in place (existing rows keep null). A future
/// version bump must add another migration branch here rather than mutating an existing shape.
/// </summary>
internal static class OutboxSchema
{
    public const int CurrentVersion = 2;

    public static void EnsureCreated(SqliteConnection connection)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = DELETE; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY,
                configuration_id TEXT NOT NULL,
                configuration_version INTEGER NOT NULL,
                configuration_revision TEXT NULL,
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

            CREATE INDEX IF NOT EXISTS ix_events_status ON events (status);
            """;
        command.ExecuteNonQuery();

        using var versionCheck = connection.CreateCommand();
        versionCheck.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        var existingVersion = (long)versionCheck.ExecuteScalar()!;
        if (existingVersion >= CurrentVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        if (!HasColumn(connection, transaction, "events", "configuration_revision"))
        {
            using var addRevision = connection.CreateCommand();
            addRevision.Transaction = transaction;
            addRevision.CommandText = "ALTER TABLE events ADD COLUMN configuration_revision TEXT NULL;";
            addRevision.ExecuteNonQuery();
        }

        using var setVersion = connection.CreateCommand();
        setVersion.Transaction = transaction;
        setVersion.CommandText = "DELETE FROM schema_version; INSERT INTO schema_version (version) VALUES ($version);";
        setVersion.Parameters.AddWithValue("$version", CurrentVersion);
        setVersion.ExecuteNonQuery();
        transaction.Commit();
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var info = connection.CreateCommand();
        info.Transaction = transaction;
        info.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        info.Parameters.AddWithValue("$column", column);
        return (long)info.ExecuteScalar()! > 0;
    }
}
