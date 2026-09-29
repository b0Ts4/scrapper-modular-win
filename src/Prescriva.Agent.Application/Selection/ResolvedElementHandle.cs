namespace Prescriva.Agent.Application.Selection;

/// <summary>
/// An opaque token referring to a specific, previously-resolved live UI element. Valid
/// only for the lifetime of that element and only meaningful to the
/// Prescriva.Agent.Windows automation code that produced it.
///
/// This type is deliberately empty and its constructor is `internal`: Application-layer
/// code (and everything above it) can hold a handle, pass it to
/// <see cref="Capture.ICaptureProvider"/>, and store it, but cannot construct one, cannot
/// subclass it (the constructor is not visible outside this assembly and
/// Prescriva.Agent.Windows, which has `InternalsVisibleTo` access), and has no way to
/// reach whatever native reference a Prescriva.Agent.Windows subclass carries. Only code
/// in Prescriva.Agent.Windows can construct the concrete subclass that actually holds an
/// `AutomationElement`, and only that same code can read it back out.
/// </summary>
public abstract class ResolvedElementHandle
{
    internal ResolvedElementHandle()
    {
    }
}
