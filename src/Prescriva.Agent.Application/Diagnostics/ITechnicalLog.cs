namespace Prescriva.Agent.Application.Diagnostics;

public enum TechnicalLogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// A single structured technical log entry. Deliberately carries no captured field value:
/// every property here is an identifier, a code, a provider name, a confidence score or a
/// timing - never the text a user typed or a value read off a live UI element. Callers
/// (notably <see cref="Prescriva.Agent.Application.Runtime.SessionCoordinator"/>) must
/// build <see cref="Message"/> the same way - describing what happened, never quoting a
/// captured value - so that this privacy guarantee holds end to end, not just in the
/// typed properties.
/// </summary>
public sealed record TechnicalLogEntry(
    TechnicalLogLevel Level,
    string Code,
    string Message,
    DateTimeOffset Timestamp,
    Guid? SessionId = null,
    string? TriggerId = null,
    string? FieldId = null,
    string? Provider = null,
    double? Confidence = null,
    TimeSpan? Elapsed = null);

/// <summary>
/// The application-facing abstraction over durable technical logging. Implementations
/// (in Prescriva.Agent.Infrastructure) own the actual sink (file, console, structured
/// store); this interface only ever accepts the plain, value-free <see cref="TechnicalLogEntry"/>.
/// </summary>
public interface ITechnicalLog
{
    void Log(TechnicalLogEntry entry);
}
