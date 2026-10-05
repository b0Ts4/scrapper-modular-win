using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Runtime;

public enum StartupDecisionKind
{
    /// <summary>Resume monitoring: the integration left active is still approved for its exact content.</summary>
    Activate,

    /// <summary>The operator left no integration monitoring.</summary>
    NothingActive,

    /// <summary>The integration left active can no longer be loaded (deleted, unreadable or invalid).</summary>
    ConfigurationUnavailable,

    /// <summary>The integration left active has no approval.</summary>
    NotApproved,

    /// <summary>The integration left active was edited after its last approved test.</summary>
    ChangedSinceTest,
}

/// <summary>What starting with Windows does, and why; only <see cref="StartupDecisionKind.Activate"/> carries a configuration.</summary>
public sealed record StartupDecision(
    StartupDecisionKind Kind,
    string? ConfigurationId = null,
    IntegrationConfiguration? Configuration = null,
    ConfigurationApproval? Approval = null)
{
    /// <summary>The visible reason shown to the operator (window status and tray tooltip).</summary>
    public string Describe() => Kind switch
    {
        StartupDecisionKind.Activate => $"Iniciado com o Windows: monitorando '{ConfigurationId}'.",
        StartupDecisionKind.NothingActive => "Iniciado com o Windows: nenhuma integração estava ativa; nada sendo monitorado.",
        StartupDecisionKind.ConfigurationUnavailable => $"Iniciado com o Windows: a integração '{ConfigurationId}' não pôde ser carregada; nada sendo monitorado.",
        StartupDecisionKind.NotApproved => $"Iniciado com o Windows: a integração '{ConfigurationId}' não tem teste aprovado; nada sendo monitorado.",
        StartupDecisionKind.ChangedSinceTest => $"Iniciado com o Windows: a integração '{ConfigurationId}' foi alterada desde o último teste aprovado; teste e aprove novamente.",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// Decides what to monitor when the Agent starts with Windows: only the integration the
/// operator left active, and only while its approval still matches its current content -
/// the same gate <see cref="AgentRuntime.ActivateAsync"/> enforces. Nothing else is ever
/// activated automatically.
/// </summary>
public sealed class StartupActivation
{
    private readonly IMonitoringPreferenceStore _preferences;
    private readonly IConfigurationStore _configurations;
    private readonly ApprovalService _approvals;

    public StartupActivation(IMonitoringPreferenceStore preferences, IConfigurationStore configurations, ApprovalService approvals)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(configurations);
        ArgumentNullException.ThrowIfNull(approvals);
        _preferences = preferences;
        _configurations = configurations;
        _approvals = approvals;
    }

    public async Task<StartupDecision> DecideAsync(CancellationToken cancellationToken)
    {
        var id = await _preferences.GetActiveConfigurationIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(id))
        {
            return new StartupDecision(StartupDecisionKind.NothingActive);
        }

        IntegrationConfiguration configuration;
        try
        {
            configuration = await _configurations.LoadAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ConfigurationValidationException or System.Text.Json.JsonException)
        {
            return new StartupDecision(StartupDecisionKind.ConfigurationUnavailable, id);
        }

        var status = await _approvals.GetStatusAsync(configuration, cancellationToken).ConfigureAwait(false);
        return status.State switch
        {
            ApprovalState.Approved => new StartupDecision(StartupDecisionKind.Activate, id, configuration, status.Approval),
            ApprovalState.ChangedSinceTest => new StartupDecision(StartupDecisionKind.ChangedSinceTest, id),
            _ => new StartupDecision(StartupDecisionKind.NotApproved, id),
        };
    }
}
