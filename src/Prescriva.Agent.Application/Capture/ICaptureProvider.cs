using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Capture;

/// <summary>
/// The application-facing abstraction over reading a value from a previously-resolved
/// live UI element. Implementations (in Prescriva.Agent.Windows) own all native UI
/// Automation pattern access; this interface and everything it returns is plain data.
/// </summary>
public interface ICaptureProvider
{
    Task<CaptureResult> CaptureAsync(ResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken);
}
