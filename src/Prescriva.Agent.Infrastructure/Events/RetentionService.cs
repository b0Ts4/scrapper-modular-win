using Prescriva.Agent.Application.Events;

namespace Prescriva.Agent.Infrastructure.Events;

/// <summary>
/// Deletes confirmed outbox events strictly older than the retention window (7 days). Pending or
/// quarantined events are never touched here — only confirmation followed by age, or an explicit
/// user action elsewhere, removes an event.
/// </summary>
public sealed class RetentionService
{
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(7);

    private readonly IEventOutbox _outbox;
    private readonly Func<DateTimeOffset> _utcNowProvider;

    public RetentionService(IEventOutbox outbox, Func<DateTimeOffset>? utcNowProvider = null)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        _outbox = outbox;
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public Task ApplyAsync(CancellationToken cancellationToken)
    {
        var threshold = _utcNowProvider() - RetentionWindow;
        return _outbox.DeleteConfirmedBeforeAsync(threshold, cancellationToken);
    }
}
