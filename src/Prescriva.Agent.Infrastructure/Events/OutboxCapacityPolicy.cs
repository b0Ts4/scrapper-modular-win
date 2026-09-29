namespace Prescriva.Agent.Infrastructure.Events;

public enum OutboxCapacityStatus
{
    Normal,
    Warning,
    Critical
}

/// <summary>
/// Diagnostic assessment of outbox pending-event pressure. Never deletes or otherwise mutates events —
/// evaluating capacity is purely a read of a pending count against configured thresholds, so callers can
/// alert an operator without ever risking silent data loss of pending (unconfirmed) events.
/// </summary>
public sealed record OutboxCapacityAssessment(
    OutboxCapacityStatus Status,
    int PendingCount,
    int WarningThreshold,
    int CriticalThreshold,
    string? Message);

public sealed class OutboxCapacityPolicy
{
    private readonly int _warningThreshold;
    private readonly int _criticalThreshold;

    public OutboxCapacityPolicy(int warningThreshold = 1000, int criticalThreshold = 5000)
    {
        if (warningThreshold < 0)
            throw new ArgumentOutOfRangeException(nameof(warningThreshold), warningThreshold, "Warning threshold cannot be negative.");
        if (criticalThreshold < warningThreshold)
            throw new ArgumentOutOfRangeException(nameof(criticalThreshold), criticalThreshold, "Critical threshold cannot be lower than the warning threshold.");

        _warningThreshold = warningThreshold;
        _criticalThreshold = criticalThreshold;
    }

    public OutboxCapacityAssessment Evaluate(int pendingCount)
    {
        if (pendingCount < 0)
            throw new ArgumentOutOfRangeException(nameof(pendingCount), pendingCount, "Pending count cannot be negative.");

        if (pendingCount >= _criticalThreshold)
        {
            return new OutboxCapacityAssessment(
                OutboxCapacityStatus.Critical,
                pendingCount,
                _warningThreshold,
                _criticalThreshold,
                $"Outbox pending count ({pendingCount}) reached the critical threshold ({_criticalThreshold}).");
        }

        if (pendingCount >= _warningThreshold)
        {
            return new OutboxCapacityAssessment(
                OutboxCapacityStatus.Warning,
                pendingCount,
                _warningThreshold,
                _criticalThreshold,
                $"Outbox pending count ({pendingCount}) reached the warning threshold ({_warningThreshold}).");
        }

        return new OutboxCapacityAssessment(
            OutboxCapacityStatus.Normal,
            pendingCount,
            _warningThreshold,
            _criticalThreshold,
            Message: null);
    }
}
