namespace Prescriva.Agent.Application.Capture;

/// <summary>The stable, typed outcomes a capture attempt can produce.</summary>
public enum CaptureOutcome
{
    /// <summary>A value was successfully read from the element via a supported pattern.</summary>
    Captured,

    /// <summary>None of the patterns this provider knows how to read were supported by the element.</summary>
    UnsupportedPattern,

    /// <summary>The element became unavailable (e.g. it was removed from the tree) during capture.</summary>
    ElementUnavailable,

    /// <summary>The operation did not complete within its allotted timeout.</summary>
    TimedOut,

    /// <summary>A file field's control shows a path that does not exist or cannot be read, or no content at all.</summary>
    FileUnavailable,

    /// <summary>A file field's content exceeds <see cref="AttachmentLimits.MaxBytes"/>; nothing was read.</summary>
    TooLarge,

    /// <summary>An image-only control is off screen or covered by another window, so it was not captured.</summary>
    Obscured,

    /// <summary>An OCR field could not be read: no OCR recognizer language is installed on this machine.</summary>
    OcrUnavailable,
}

/// <summary>
/// A single attempt to read a value through one named UI Automation pattern, kept as a
/// typed history so callers (and tests) can see which patterns were tried, in what order,
/// and why any that failed did not produce a value - e.g. to confirm a control without
/// `ValuePattern` fell through to a compatible pattern instead of failing outright.
/// </summary>
public sealed record PatternAttempt(string PatternName, bool Succeeded, string? FailureReason = null);

/// <summary>
/// The outcome of a single capture request. Only <see cref="CaptureOutcome.Captured"/>
/// carries a <see cref="Value"/>; every other outcome is a stable, typed failure that
/// callers can branch on without catching native UI Automation exceptions.
/// </summary>
public sealed record CaptureResult(
    CaptureOutcome Outcome,
    string? Value,
    string ProviderId,
    double Confidence,
    TimeSpan Duration,
    IReadOnlyList<PatternAttempt> Attempts,
    CapturedAttachment? Attachment = null)
{
    /// <summary>The provider ID reported by every capture produced by the UI Automation provider.</summary>
    public const string UiaProviderId = "uia";
}
