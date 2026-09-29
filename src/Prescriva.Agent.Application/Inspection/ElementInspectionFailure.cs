namespace Prescriva.Agent.Application.Inspection;

/// <summary>
/// The stable, typed reasons an element-inspection operation can fail. Callers branch
/// on this instead of catching raw COM/UIA exception types - and, because this type
/// lives in the Application layer, an Application-layer consumer of
/// <see cref="IElementInspector"/> can branch on it without taking a compile-time
/// dependency on Prescriva.Agent.Windows.
/// </summary>
public enum ElementInspectionFailureKind
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
/// A stable, typed failure that <see cref="IElementInspector"/> implementations throw
/// when they cannot express the failure in-band through their return type (for example,
/// <see cref="IElementInspector.FindCandidatesAsync"/> returns a plain list with no room
/// for a failure code). Declared in the Application layer, alongside the interface it
/// serves, so catching it never requires a reference to Prescriva.Agent.Windows.
/// </summary>
public sealed class ElementInspectionFailure : Exception
{
    public ElementInspectionFailure(ElementInspectionFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ElementInspectionFailureKind Kind { get; }
}
