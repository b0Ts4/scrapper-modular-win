using System.Globalization;
using System.Text.Json;
using Prescriva.Agent.Application.Diagnostics;

namespace Prescriva.Agent.Infrastructure.Diagnostics;

/// <summary>
/// Writes each <see cref="TechnicalLogEntry"/> as one structured JSON line to a
/// <see cref="TextWriter"/> (an append-mode file by default). Serializes exactly the
/// properties <see cref="TechnicalLogEntry"/> exposes - level, code, message, timestamp,
/// session/trigger/field IDs, provider, confidence and elapsed time - and nothing else, so
/// it can never leak a captured field value that was never handed to it in the first
/// place; that guarantee is upheld by callers (see <see cref="TechnicalLogEntry"/>'s own
/// remarks), not reconstructed here.
/// </summary>
public sealed class StructuredTechnicalLog : ITechnicalLog, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();

    public StructuredTechnicalLog(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _ownsWriter = false;
    }

    public StructuredTechnicalLog(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Opened at the end rather than with FileMode.Append, which forbids the truncation
        // Clear needs; writes still always go to the end of the file.
        var stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        stream.Seek(0, SeekOrigin.End);
        _writer = new StreamWriter(stream);
        _ownsWriter = true;
    }

    public void Log(TechnicalLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var line = JsonSerializer.Serialize(new StructuredLogLine(
            entry.Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            entry.Level.ToString(),
            entry.Code,
            entry.Message,
            entry.SessionId,
            entry.TriggerId,
            entry.FieldId,
            entry.Provider,
            entry.Confidence,
            entry.Elapsed?.TotalMilliseconds), JsonOptions);

        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    /// <summary>
    /// Empties the log file (part of the operator's explicit "clear local data"). Logging
    /// keeps working afterwards. Only supported for a log that owns its file.
    /// </summary>
    public void Clear()
    {
        if (!_ownsWriter || _writer is not StreamWriter { BaseStream: FileStream stream })
        {
            throw new NotSupportedException("Only a file-backed technical log can be cleared.");
        }

        lock (_gate)
        {
            _writer.Flush();
            stream.SetLength(0);
            stream.Flush();
        }
    }

    public void Dispose()
    {
        if (_ownsWriter)
        {
            _writer.Dispose();
        }
    }

    private sealed record StructuredLogLine(
        string Timestamp,
        string Level,
        string Code,
        string Message,
        Guid? SessionId,
        string? TriggerId,
        string? FieldId,
        string? Provider,
        double? Confidence,
        double? ElapsedMs);
}
