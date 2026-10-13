using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Inspection;

/// <summary>Which element under the pointer an inspection reports.</summary>
public enum InspectionDepth
{
    /// <summary>
    /// The element the operator most likely means: an anonymous caption is promoted to the
    /// button or list item containing it (the default).
    /// </summary>
    Interactive = 0,

    /// <summary>
    /// The smallest element under the pointer, looking inside the hit element and without
    /// promotion - for small fields inside a larger one (the operator holds Shift).
    /// </summary>
    Innermost = 1,
}

/// <summary>
/// The application-facing abstraction over live UI element inspection. Implementations
/// (in Prescriva.Agent.Windows) own all native UI Automation access; this interface and
/// everything it returns is plain data so the rest of the application never needs to
/// reference System.Windows.Automation.
/// </summary>
public interface IElementInspector
{
    Task<InspectionResult> FromPointAsync(ScreenPoint point, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Inspects at the requested <paramref name="depth"/>; by default, the interactive element.</summary>
    Task<InspectionResult> FromPointAsync(ScreenPoint point, InspectionDepth depth, TimeSpan timeout, CancellationToken cancellationToken) =>
        FromPointAsync(point, timeout, cancellationToken);

    Task<IReadOnlyList<ElementSnapshot>> FindCandidatesAsync(
        ApplicationDefinition application,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
