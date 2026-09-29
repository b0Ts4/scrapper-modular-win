using Microsoft.Data.Sqlite;

namespace Prescriva.Agent.Infrastructure.Events;

/// <summary>
/// Creates and migrates the event outbox SQLite schema. Version 1 is the only version today; a future
/// version bump must add a migration branch here rather than mutating the version 1 table shape in place.
/// </summary>
internal static class OutboxSchema
{
    public const int CurrentVersion = 1;

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
        versionCheck.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var existingRows = (long)versionCheck.ExecuteScalar()!;
        if (existingRows == 0)
        {
            using var insertVersion = connection.CreateCommand();
            insertVersion.CommandText = "INSERT INTO schema_version (version) VALUES ($version);";
            insertVersion.Parameters.AddWithValue("$version", CurrentVersion);
            insertVersion.ExecuteNonQuery();
        }
    }
}
