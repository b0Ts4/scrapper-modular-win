namespace Prescriva.Agent.Domain.Sessions;

public enum CaptureFailure
{
    Unavailable,
    Ambiguous,
    Unreadable,
    UnsupportedProvider,
    ProviderFailed
}

public sealed record CapturedFieldValue(string? Value, CaptureFailure? Failure = null);
