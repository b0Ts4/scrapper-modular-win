namespace Prescriva.Agent.Application.Jobs;

/// <summary>
/// The robot at this pharmacy (plan 22): on or off, its level, the employee code it answers
/// "Funcionário Recepção" with, its local API (port and token) and, when set, the CRM it pulls
/// jobs from, and the playbook it launches quotes with. Tokens are secrets: stored protected, never logged.
/// </summary>
public sealed record RobotConfiguration(
    bool Enabled,
    RobotLevel Level,
    string EmployeeCode,
    int LocalApiPort,
    string LocalApiToken,
    string? CrmUrl,
    string? CrmToken,
    string QuotePlaybookId = RobotConfiguration.DefaultQuotePlaybook)
{
    public const int DefaultLocalApiPort = 8765;

    public const string DefaultQuotePlaybook = "formulacerta_lancar_formula";

    public RobotSettings ToSettings() => new(EmployeeCode, Level, QuotePlaybookId);
}

public interface IRobotConfigurationStore
{
    /// <summary>The saved settings, or the defaults (off, assisted, a new local token) when there are none.</summary>
    Task<RobotConfiguration> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(RobotConfiguration configuration, CancellationToken cancellationToken);
}
