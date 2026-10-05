using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Security;

namespace Prescriva.Agent.Infrastructure.Events;

/// <summary>
/// SQLite-backed <see cref="IAttachmentStore"/>, sharing the event database file. The content
/// and the original file name (which may itself identify a patient) are encrypted with the
/// <see cref="IPayloadProtector"/>; only the content hash, size, type and source are plain.
/// </summary>
public sealed class SqliteAttachmentStore : IAttachmentStore
{
    private readonly string _connectionString;
    private readonly IPayloadProtector _protector;

    public SqliteAttachmentStore(string databasePath, IPayloadProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(protector);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, DefaultTimeout = 2 }.ToString();
        _protector = protector;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS attachments (
                sha256 TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                content_type TEXT NOT NULL,
                source TEXT NOT NULL,
                file_name_ciphertext BLOB NOT NULL,
                content_ciphertext BLOB NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<AttachmentInfo> SaveAsync(CapturedAttachment attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(attachment.Content);
        if (attachment.Content.LongLength > AttachmentLimits.MaxBytes)
        {
            throw new AttachmentTooLargeException(attachment.Content.LongLength);
        }

        var hash = Convert.ToHexString(SHA256.HashData(attachment.Content));
        var info = new AttachmentInfo(AttachmentReference.For(hash), attachment.FileName, attachment.ContentType, attachment.Content.LongLength, attachment.Source);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO attachments (sha256, size, content_type, source, file_name_ciphertext, content_ciphertext)
            VALUES ($sha256, $size, $contentType, $source, $fileName, $content);
            """;
        command.Parameters.AddWithValue("$sha256", hash);
        command.Parameters.AddWithValue("$size", attachment.Content.LongLength);
        command.Parameters.AddWithValue("$contentType", attachment.ContentType);
        command.Parameters.AddWithValue("$source", attachment.Source.ToString());
        command.Parameters.AddWithValue("$fileName", _protector.Protect(Encoding.UTF8.GetBytes(attachment.FileName)));
        command.Parameters.AddWithValue("$content", _protector.Protect(attachment.Content));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return info;
    }

    public async Task<AttachmentInfo?> GetInfoAsync(string reference, CancellationToken cancellationToken)
    {
        if (!AttachmentReference.IsReference(reference)) return null;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT size, content_type, source, file_name_ciphertext FROM attachments WHERE sha256 = $sha256;";
        command.Parameters.AddWithValue("$sha256", AttachmentReference.Hash(reference));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var fileName = Encoding.UTF8.GetString(_protector.Unprotect((byte[])reader["file_name_ciphertext"]));
        return new AttachmentInfo(
            reference,
            fileName,
            reader.GetString(1),
            reader.GetInt64(0),
            Enum.Parse<AttachmentSource>(reader.GetString(2)));
    }

    public async Task<byte[]?> ReadAsync(string reference, CancellationToken cancellationToken)
    {
        if (!AttachmentReference.IsReference(reference)) return null;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT content_ciphertext FROM attachments WHERE sha256 = $sha256;";
        command.Parameters.AddWithValue("$sha256", AttachmentReference.Hash(reference));
        var ciphertext = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        return ciphertext is null ? null : _protector.Unprotect(ciphertext);
    }

    public async Task<int> DeleteUnreferencedAsync(IReadOnlyCollection<string> referenced, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(referenced);
        var keep = referenced.Where(AttachmentReference.IsReference).Select(AttachmentReference.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var connection = OpenConnection();
        var all = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT sha256 FROM attachments;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) all.Add(reader.GetString(0));
        }

        var deleted = 0;
        foreach (var hash in all.Where(hash => !keep.Contains(hash)))
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM attachments WHERE sha256 = $sha256;";
            delete.Parameters.AddWithValue("$sha256", hash);
            deleted += await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        if (deleted > 0) await VacuumAsync(connection, cancellationToken);
        return deleted;
    }

    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken)
    {
        using var connection = OpenConnection();
        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM attachments;";
        var deleted = await delete.ExecuteNonQueryAsync(cancellationToken);
        await VacuumAsync(connection, cancellationToken);
        return deleted;
    }

    private static async Task VacuumAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        await vacuum.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
