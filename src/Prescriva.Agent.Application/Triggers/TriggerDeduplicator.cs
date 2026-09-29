namespace Prescriva.Agent.Application.Triggers;

/// <summary>
/// Collapses duplicate <see cref="TriggerSignal"/>s - a double click, or a UIA control
/// that fires its "invoked" event twice for a single genuine user action - into a
/// single logical signal per configured trigger.
///
/// This is pure logic: no UI Automation, no I/O. It keys strictly on signal identity and
/// timing metadata (session, trigger ID, native occurrence identity, and a time window)
/// and never inspects any captured field value - by construction, <see cref="TriggerSignal"/>
/// carries no captured value for it to look at in the first place.
/// </summary>
public sealed class TriggerDeduplicator
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid SessionId, string TriggerId), Occurrence> _lastAccepted = new();

    /// <param name="window">
    /// How close in time two signals for the same (session, trigger) must be to be
    /// treated as one duplicate occurrence. Defaults to 500ms, matching the typical
    /// Windows double-click interval.
    /// </param>
    public TriggerDeduplicator(TimeSpan? window = null)
    {
        _window = window ?? DefaultWindow;
    }

    /// <summary>
    /// Returns true if <paramref name="signal"/> should be processed as a new logical
    /// occurrence, or false if it is a duplicate of the most recently accepted signal
    /// for the same session and trigger.
    /// </summary>
    public bool Accept(TriggerSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);

        var key = (signal.SessionId, signal.TriggerId);

        lock (_gate)
        {
            if (_lastAccepted.TryGetValue(key, out var last) && IsDuplicate(last, signal))
            {
                return false;
            }

            _lastAccepted[key] = new Occurrence(signal.OccurredAt, signal.NativeOccurrenceId);
            return true;
        }
    }

    private bool IsDuplicate(Occurrence last, TriggerSignal signal)
    {
        // Same native occurrence identity reported twice (e.g. the same UIA event
        // delivered through more than one subscription) is always a duplicate,
        // regardless of the time window.
        if (last.NativeOccurrenceId is not null &&
            signal.NativeOccurrenceId is not null &&
            string.Equals(last.NativeOccurrenceId, signal.NativeOccurrenceId, StringComparison.Ordinal))
        {
            return true;
        }

        return (signal.OccurredAt - last.OccurredAt).Duration() < _window;
    }

    private readonly record struct Occurrence(DateTimeOffset OccurredAt, string? NativeOccurrenceId);
}
