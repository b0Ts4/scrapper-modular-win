using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Selection;

/// <summary>
/// The application-facing abstraction over resolving a selector fingerprint against the
/// live desktop. Implementations (in Prescriva.Agent.Windows) combine real UI Automation
/// enumeration with <see cref="SelectorMatcher"/>'s deterministic scoring to produce a
/// <see cref="SelectorResolution"/>.
/// </summary>
public interface ISelectorResolver
{
    Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken);
}
