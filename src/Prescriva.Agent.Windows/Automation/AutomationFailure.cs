namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// The stable, typed reasons a dispatched UI Automation operation can fail. Callers
/// branch on this instead of catching raw COM/UIA exception types.
/// </summary>
public enum AutomationFailureKind
{
    /// <summary>The operation, or the request queued behind it, was cancelled before it ran to completion.</summary>
    Cancelled,

    /// <summary>The operation did not complete within its allotted timeout.</summary>
    TimedOut,

    /// <summary>The target window could not be found (never launched, or already closed).</summary>
    WindowMissing,

    /// <summary>A previously located element became unavailable (e.g. it was removed from the tree) or an unexpected error occurred while inspecting it.</summary>
    ElementUnavailable,
}

/// <summary>
/// A stable, typed failure surfaced at the boundary of <see cref="AutomationDispatcher"/>.
/// Every failure a dispatched operation can produce - cancellation, timeout, a missing
/// window, or a raw COM/UIA exception - is translated into one of these before it
/// crosses out of the dispatcher, so callers never need to catch native UI Automation
/// exception types.
/// </summary>
public sealed class AutomationFailure : Exception
{
    public AutomationFailure(AutomationFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public AutomationFailureKind Kind { get; }
}
