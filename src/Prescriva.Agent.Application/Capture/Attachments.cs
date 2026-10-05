namespace Prescriva.Agent.Application.Capture;

/// <summary>Where a captured file field's content came from.</summary>
public enum AttachmentSource
{
    /// <summary>The original file, read from the path the control exposes (an exact copy).</summary>
    File,

    /// <summary>The image the control shows on screen (used when no file path is exposed).</summary>
    Screen,
}

/// <summary>The content captured for a <c>FieldKind.File</c> field. Business data: never logged.</summary>
public sealed record CapturedAttachment(byte[] Content, string FileName, string ContentType, AttachmentSource Source);

/// <summary>Metadata of a stored attachment, for display; the content stays in the store.</summary>
public sealed record AttachmentInfo(string Reference, string FileName, string ContentType, long Size, AttachmentSource Source);

public static class AttachmentLimits
{
    /// <summary>Largest attachment accepted (10 MB, decided 2026-10-05).</summary>
    public const long MaxBytes = 10L * 1024 * 1024;
}

/// <summary>Raised when an attachment exceeds <see cref="AttachmentLimits.MaxBytes"/>; nothing is stored.</summary>
public sealed class AttachmentTooLargeException(long size)
    : Exception($"The attachment has {size} bytes; the limit is {AttachmentLimits.MaxBytes} bytes.")
{
    public long Size { get; } = size;
}

/// <summary>
/// The value a file field contributes to an event: <c>attachment:&lt;SHA-256&gt;</c>. The
/// event payload carries only this reference; the content lives in <see cref="IAttachmentStore"/>.
/// </summary>
public static class AttachmentReference
{
    public const string Prefix = "attachment:";

    public static string For(string sha256Hex) => Prefix + sha256Hex.ToUpperInvariant();

    public static bool IsReference(string? value) =>
        value is not null &&
        value.StartsWith(Prefix, StringComparison.Ordinal) &&
        value.Length == Prefix.Length + 64 &&
        value.AsSpan(Prefix.Length).ToString().All(Uri.IsHexDigit);

    public static string Hash(string reference) => reference[Prefix.Length..];
}

/// <summary>
/// Stores captured attachments protected at rest, de-duplicated by content hash. Callers
/// save an attachment before the event that references it is appended.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>Stores the content (or finds it already stored) and returns its metadata and reference. Throws <see cref="AttachmentTooLargeException"/> above the limit.</summary>
    Task<AttachmentInfo> SaveAsync(CapturedAttachment attachment, CancellationToken cancellationToken);

    Task<AttachmentInfo?> GetInfoAsync(string reference, CancellationToken cancellationToken);

    Task<byte[]?> ReadAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Deletes every attachment not in <paramref name="referenced"/>; returns how many.</summary>
    Task<int> DeleteUnreferencedAsync(IReadOnlyCollection<string> referenced, CancellationToken cancellationToken);

    /// <summary>Deletes every attachment (explicit local-data cleanup); returns how many.</summary>
    Task<int> DeleteAllAsync(CancellationToken cancellationToken);
}
