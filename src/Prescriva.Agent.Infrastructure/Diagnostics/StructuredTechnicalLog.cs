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

        _writer = new StreamWriter(new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read));
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
