namespace Prescriva.Agent.Application.Configuration;

/// <summary>
/// Remembers which integration the operator left monitoring, so that starting with Windows
/// can resume exactly that one. Holds only a configuration ID - never business data.
/// </summary>
public interface IMonitoringPreferenceStore
{
    /// <summary>The integration left active, or null when the operator stopped monitoring (or never started it).</summary>
    Task<string?> GetActiveConfigurationIdAsync(CancellationToken cancellationToken);

    Task SetActiveConfigurationIdAsync(string? configurationId, CancellationToken cancellationToken);
}
