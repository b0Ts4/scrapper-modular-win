namespace Prescriva.Agent.Application.Runtime;

/// <summary>
/// Bounds how a transient field failure (element or window not found yet, resolution or read
/// timed out, element temporarily unavailable or covered) is retried when a trigger fires:
/// at most <see cref="MaxAttempts"/> attempts in total, waiting <see cref="Delays"/>[n] before
/// retry n+1 (the last delay repeats). Each attempt re-runs the full selector resolution, so a
/// retry can never reuse a stale element. Failures that need the operator - an ambiguous
/// selector, an unsupported control, a file that is too large or missing - are never retried.
/// </summary>
public sealed record CaptureRetryPolicy(int MaxAttempts, IReadOnlyList<TimeSpan> Delays)
{
    /// <summary>3 attempts, 100 ms then 200 ms apart: short, because the application may change the screen right after the click.</summary>
    public static CaptureRetryPolicy Default { get; } = new(3, [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)]);

    public TimeSpan DelayBefore(int retry) =>
        Delays.Count == 0 ? TimeSpan.Zero : Delays[Math.Min(retry, Delays.Count) - 1];
}
