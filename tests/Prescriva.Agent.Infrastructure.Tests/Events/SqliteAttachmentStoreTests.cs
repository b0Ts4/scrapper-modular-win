using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Security;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;

namespace Prescriva.Agent.Infrastructure.Tests.Events;

/// <summary>
/// Captured files and images are business data: stored separately from the event payload,
/// encrypted at rest (content and file name), de-duplicated by content hash, bounded at
/// 10 MB, and removable by the explicit cleanup and by attachment garbage collection.
/// </summary>
public sealed class SqliteAttachmentStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-attachment-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public SqliteAttachmentStoreTests()
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
    public async Task Saved_content_is_read_back_byte_for_byte_with_its_metadata()
    {
        var store = new SqliteAttachmentStore(_dbPath, new ReversibleProtector());
        var content = RandomNumberGenerator.GetBytes(300_000);

        var info = await store.SaveAsync(new CapturedAttachment(content, "receita-joao.pdf", "application/pdf", AttachmentSource.File), CancellationToken.None);

        Assert.Equal(AttachmentReference.For(Convert.ToHexString(SHA256.HashData(content))), info.Reference);
        Assert.True(AttachmentReference.IsReference(info.Reference));
        Assert.Equal(content.Length, info.Size);
        var reopened = new SqliteAttachmentStore(_dbPath, new ReversibleProtector());
        Assert.Equal(content, await reopened.ReadAsync(info.Reference, CancellationToken.None));
        var readInfo = await reopened.GetInfoAsync(info.Reference, CancellationToken.None);
        Assert.Equal("receita-joao.pdf", readInfo!.FileName);
        Assert.Equal("application/pdf", readInfo.ContentType);
        Assert.Equal(AttachmentSource.File, readInfo.Source);
    }

    [Fact]
    public async Task Identical_content_is_stored_once()
    {
        var store = new SqliteAttachmentStore(_dbPath, new ReversibleProtector());
        var content = Encoding.UTF8.GetBytes("mesma imagem");

        var first = await store.SaveAsync(new CapturedAttachment(content, "a.png", "image/png", AttachmentSource.Screen), CancellationToken.None);
        var second = await store.SaveAsync(new CapturedAttachment(content, "a.png", "image/png", AttachmentSource.Screen), CancellationToken.None);

        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(1, await CountRowsAsync());
    }

    [Fact]
    public async Task Content_larger_than_10_MB_is_refused_and_nothing_is_stored()
    {
        var store = new SqliteAttachmentStore(_dbPath, new ReversibleProtector());
        var content = new byte[AttachmentLimits.MaxBytes + 1];

        await Assert.ThrowsAsync<AttachmentTooLargeException>(() =>
            store.SaveAsync(new CapturedAttachment(content, "grande.pdf", "application/pdf", AttachmentSource.File), CancellationToken.None));
        Assert.Equal(0, await CountRowsAsync());
    }

    [Fact]
    public async Task Unknown_or_malformed_references_read_as_null()
    {
        var store = new SqliteAttachmentStore(_dbPath, new ReversibleProtector());

        Assert.Null(await store.ReadAsync(AttachmentReference.For(new string('A', 64)), CancellationToken.None));
        Assert.Null(await store.GetInfoAsync("not-a-reference", CancellationToken.None));
    }

    [Fact]
    public async Task Garbage_collection_keeps_referenced_attachments_and_cleanup_removes_all()
    {
        var store = new SqliteAttachmentStore(_dbPath, new ReversibleProtector());
        var kept = await store.SaveAsync(new CapturedAttachment([1, 2, 3], "kept.png", "image/png", AttachmentSource.Screen), CancellationToken.None);
        var orphan = await store.SaveAsync(new CapturedAttachment([4, 5, 6], "orphan.png", "image/png", AttachmentSource.Screen), CancellationToken.None);

        Assert.Equal(1, await store.DeleteUnreferencedAsync([kept.Reference], CancellationToken.None));
        Assert.NotNull(await store.ReadAsync(kept.Reference, CancellationToken.None));
        Assert.Null(await store.ReadAsync(orphan.Reference, CancellationToken.None));

        Assert.Equal(1, await store.DeleteAllAsync(CancellationToken.None));
        Assert.Equal(0, await CountRowsAsync());
    }

    [Fact]
    public async Task Neither_the_content_nor_the_file_name_appears_in_plaintext_in_the_database()
    {
        var store = new SqliteAttachmentStore(_dbPath, new DpapiPayloadProtector());
        var content = Encoding.UTF8.GetBytes("CONTEUDO-SECRETO-DA-RECEITA-6f1a2b");

        var info = await store.SaveAsync(new CapturedAttachment(content, "receita-paciente-6f1a2b.pdf", "application/pdf", AttachmentSource.File), CancellationToken.None);
        SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(_dbPath);

        Assert.True(bytes.AsSpan().IndexOf(content) < 0, "Attachment content stored in plaintext.");
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("receita-paciente-6f1a2b")) < 0, "File name stored in plaintext.");
        Assert.Equal(content, await store.ReadAsync(info.Reference, CancellationToken.None));
    }

    private async Task<long> CountRowsAsync()
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM attachments;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Test-only protector (XOR) so the store's behavior is testable without DPAPI.</summary>
    private sealed class ReversibleProtector : IPayloadProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();

        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}
