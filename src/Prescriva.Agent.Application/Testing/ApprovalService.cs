using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Testing;

/// <summary>Whether a configuration's current content may be activated.</summary>
public enum ApprovalState
{
    /// <summary>No approval was ever recorded for this configuration.</summary>
    NotTested,

    /// <summary>An approval exists for exactly the current content.</summary>
    Approved,

    /// <summary>An approval exists, but for different content: the configuration was edited after its last passing test.</summary>
    ChangedSinceTest,
}

/// <summary>
/// The approval decision for one configuration. <see cref="Approval"/> is non-null only when
/// <see cref="State"/> is <see cref="ApprovalState.Approved"/> - it is the value to hand to
/// <c>AgentRuntime.ActivateAsync</c>. <see cref="LastApprovedAtUtc"/> tells the operator when
/// the configuration last passed, even if it was edited since.
/// </summary>
public sealed record ApprovalStatus(ApprovalState State, ConfigurationApproval? Approval, DateTimeOffset? LastApprovedAtUtc);

/// <summary>
/// Records approvals earned by passing test runs and decides, for a configuration's current
/// content, whether a stored approval still applies. The activation gate itself stays in
/// <c>AgentRuntime</c>; this service only finds the approval to give it.
/// </summary>
public sealed class ApprovalService
{
    private readonly IApprovalStore _store;
    private readonly Func<DateTimeOffset> _clock;

    public ApprovalService(IApprovalStore store, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Stores the approval a passing report earns and returns it. Throws
    /// <see cref="InvalidOperationException"/> for a report that did not pass every check;
    /// the previously stored approval, if any, is left untouched.
    /// </summary>
    public async Task<ConfigurationApproval> RecordAsync(IntegrationTestReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var approval = report.ToApproval(_clock())
            ?? throw new InvalidOperationException("Cannot approve: the test run did not pass every check.");

        await _store.SaveAsync(approval, cancellationToken).ConfigureAwait(false);
        return approval;
    }

    public async Task<ApprovalStatus> GetStatusAsync(IntegrationConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var stored = await _store.LoadAsync(configuration.Id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return new ApprovalStatus(ApprovalState.NotTested, null, null);
        }

        return stored.IsValidFor(ConfigurationFingerprint.Compute(configuration))
            ? new ApprovalStatus(ApprovalState.Approved, stored, stored.ApprovedAtUtc)
            : new ApprovalStatus(ApprovalState.ChangedSinceTest, null, stored.ApprovedAtUtc);
    }
}
