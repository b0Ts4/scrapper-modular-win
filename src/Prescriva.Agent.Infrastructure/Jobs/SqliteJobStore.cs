using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Application.Jobs;
using Prescriva.Agent.Application.Security;

namespace Prescriva.Agent.Infrastructure.Jobs;

/// <summary>
/// The robot's queue in SQLite (plan 22). Searchable metadata (id, kind, state, times, a summary
/// without values) is plain; the request and the result - contract JSON with patient and
/// prescription data - are encrypted with <see cref="IPayloadProtector"/> (DPAPI).
/// A job found <c>Running</c> when the store opens was cut by a crash or a restart: the screen may
/// be half filled, so it becomes <c>Stopped</c> for a person to look at, never run again by itself.
/// </summary>
public sealed class SqliteJobStore : IJobStore
{
    private readonly string _connectionString;
    private readonly IPayloadProtector _protector;

    public SqliteJobStore(string databasePath, IPayloadProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, DefaultTimeout = 2 }.ToString();

        using var connection = Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS jobs (
                    seq INTEGER PRIMARY KEY AUTOINCREMENT,
                    id TEXT NOT NULL UNIQUE,
                    kind TEXT NOT NULL,
                    state TEXT NOT NULL,
                    request_ciphertext BLOB NOT NULL,
                    result_ciphertext BLOB NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    reported INTEGER NOT NULL DEFAULT 0);
                """;
            create.ExecuteNonQuery();
        }

        using var interrupted = connection.CreateCommand();
        interrupted.CommandText = "SELECT id FROM jobs WHERE state = 'Running';";
        var ids = new List<string>();
        using (var reader = interrupted.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        foreach (var id in ids)
        {
            using var stop = connection.CreateCommand();
            stop.CommandText = "UPDATE jobs SET state = 'Stopped', result_ciphertext = $result, updated_at = $now WHERE id = $id;";
            stop.Parameters.AddWithValue("$id", id);
            stop.Parameters.AddWithValue("$result", _protector.Protect(Encoding.UTF8.GetBytes(JobJson.Write(
                new JobResultDocument(id, "stopped", [], new JobStop(0, null, "Interrupted", "The Agent stopped while the robot was working."))))));
            stop.Parameters.AddWithValue("$now", Now());
            stop.ExecuteNonQuery();
        }
    }

    public async Task<(RobotJob Job, bool Added)> AddAsync(RobotJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var connection = Open();
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT OR IGNORE INTO jobs (id, kind, state, request_ciphertext, result_ciphertext, created_at, updated_at, summary, reported)
            VALUES ($id, $kind, $state, $request, $result, $created, $updated, $summary, $reported);
            """;
        Bind(insert, job);
        var added = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        return (added ? job : (await GetAsync(job.Id, cancellationToken).ConfigureAwait(false))!, added);
    }

    public async Task<RobotJob?> GetAsync(string id, CancellationToken cancellationToken) =>
        (await QueryAsync("WHERE id = $id", command => command.Parameters.AddWithValue("$id", id), cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    public async Task<RobotJob?> NextQueuedAsync(CancellationToken cancellationToken) =>
        (await QueryAsync("WHERE state = 'Queued' ORDER BY seq ASC LIMIT 1", _ => { }, cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    public async Task UpdateAsync(RobotJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var connection = Open();
        using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE jobs SET kind = $kind, state = $state, request_ciphertext = $request, result_ciphertext = $result,
                updated_at = $updated, summary = $summary, reported = $reported
            WHERE id = $id;
            """;
        Bind(update, job);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RobotJob>> RecentAsync(int count, CancellationToken cancellationToken) =>
        QueryAsync("ORDER BY seq DESC LIMIT $count", command => command.Parameters.AddWithValue("$count", count), cancellationToken);

    public Task<IReadOnlyList<RobotJob>> UnreportedAsync(CancellationToken cancellationToken) =>
        QueryAsync("WHERE reported = 0 AND state IN ('Completed', 'Stopped', 'Cancelled') ORDER BY seq ASC", _ => { }, cancellationToken);

    private async Task<IReadOnlyList<RobotJob>> QueryAsync(string where, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        using var connection = Open();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id, kind, state, request_ciphertext, result_ciphertext, created_at, updated_at, summary, reported FROM jobs " + where + ";";
        bind(select);
        var jobs = new List<RobotJob>();
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(new RobotJob(
                reader.GetString(0),
                Enum.Parse<RobotJobKind>(reader.GetString(1)),
                Enum.Parse<RobotJobState>(reader.GetString(2)),
                Decrypt((byte[])reader.GetValue(3)),
                reader.IsDBNull(4) ? null : Decrypt((byte[])reader.GetValue(4)),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
                reader.GetString(7),
                reader.GetInt64(8) != 0));
        }

        return jobs;
    }

    private void Bind(SqliteCommand command, RobotJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$kind", job.Kind.ToString());
        command.Parameters.AddWithValue("$state", job.State.ToString());
        command.Parameters.AddWithValue("$request", _protector.Protect(Encoding.UTF8.GetBytes(job.Request)));
        command.Parameters.AddWithValue("$result", job.Result is null ? DBNull.Value : _protector.Protect(Encoding.UTF8.GetBytes(job.Result)));
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$summary", job.Summary);
        command.Parameters.AddWithValue("$reported", job.Reported ? 1 : 0);
    }

    private string Decrypt(byte[] ciphertext) => Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));

    private static string Now() => DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
