using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Configuration;

/// <summary>
/// Persists the most recent <see cref="ConfigurationApproval"/> per configuration, so a
/// tested configuration does not have to be retested after the Agent restarts. Storing an
/// approval never weakens the activation gate: an approval only ever matches the exact
/// content hash it was granted for.
/// </summary>
public interface IApprovalStore
{
    Task SaveAsync(ConfigurationApproval approval, CancellationToken cancellationToken);

    /// <summary>The stored approval, or null when there is none or it cannot be trusted (unreadable, incomplete, or for another configuration).</summary>
    Task<ConfigurationApproval?> LoadAsync(string configurationId, CancellationToken cancellationToken);

    /// <summary>Removes the stored approval, if any.</summary>
    Task DeleteAsync(string configurationId, CancellationToken cancellationToken);
}
