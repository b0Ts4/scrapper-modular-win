namespace Prescriva.Agent.Application.Selection;

/// <summary>
/// The outcome of a single selector-resolution request. Only
/// <see cref="SelectorResolutionStatus.Found"/> carries a <see cref="Handle"/>; every
/// other outcome is a stable, typed result that callers can branch on without catching
/// native UI Automation exceptions.
/// </summary>
public enum SelectorResolutionStatus
{
    /// <summary>Exactly one candidate scored high enough, with a clear lead over the next best.</summary>
    Found,

    /// <summary>The best-scoring candidate did not have a clear enough lead over the next best; resolution is not safe to act on.</summary>
    Ambiguous,

    /// <summary>No candidate scored high enough to be considered a match.</summary>
    NotFound,

    /// <summary>The target window could not be found (never launched, or already closed).</summary>
    WindowMissing,

    /// <summary>The operation did not complete within its allotted timeout.</summary>
    TimedOut,
}

public sealed record SelectorResolution(
    SelectorResolutionStatus Status,
    ResolvedElementHandle? Handle = null,
    double Confidence = 0,
    string? FailureReason = null)
{
    public static SelectorResolution Found(ResolvedElementHandle handle, double confidence) =>
        new(SelectorResolutionStatus.Found, handle, confidence);

    public static SelectorResolution Ambiguous(double confidence) =>
        new(SelectorResolutionStatus.Ambiguous, Confidence: confidence);

    public static SelectorResolution NotFound(string? reason = null) =>
        new(SelectorResolutionStatus.NotFound, FailureReason: reason);

    public static SelectorResolution WindowMissing(string? reason = null) =>
        new(SelectorResolutionStatus.WindowMissing, FailureReason: reason);

    public static SelectorResolution TimedOut() =>
        new(SelectorResolutionStatus.TimedOut);
}
