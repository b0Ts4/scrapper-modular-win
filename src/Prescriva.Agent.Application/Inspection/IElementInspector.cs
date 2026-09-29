using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Inspection;

/// <summary>
/// The application-facing abstraction over live UI element inspection. Implementations
/// (in Prescriva.Agent.Windows) own all native UI Automation access; this interface and
/// everything it returns is plain data so the rest of the application never needs to
/// reference System.Windows.Automation.
/// </summary>
public interface IElementInspector
{
    Task<InspectionResult> FromPointAsync(ScreenPoint point, TimeSpan timeout, CancellationToken cancellationToken);

    Task<IReadOnlyList<ElementSnapshot>> FindCandidatesAsync(
        ApplicationDefinition application,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
