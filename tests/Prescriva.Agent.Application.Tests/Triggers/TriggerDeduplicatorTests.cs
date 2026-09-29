using Prescriva.Agent.Application.Triggers;

namespace Prescriva.Agent.Application.Tests.Triggers;

/// <summary>
/// Exercises TriggerDeduplicator in isolation - pure logic, no UI Automation involved.
/// Covers the review focus item directly: a double click or duplicate UIA event must
/// collapse into exactly one logical signal, never two.
/// </summary>
public sealed class TriggerDeduplicatorTests
{
    private static readonly Guid SessionA = Guid.NewGuid();
    private static readonly Guid SessionB = Guid.NewGuid();

    [Fact]
    public void Accept_returns_true_for_the_first_signal_seen_for_a_trigger()
    {
        var deduplicator = new TriggerDeduplicator(TimeSpan.FromMilliseconds(500));

        var accepted = deduplicator.Accept(new TriggerSignal(SessionA, "add-item", DateTimeOffset.UtcNow));

        Assert.True(accepted);
    }

    [Fact]
    public void Accept_rejects_a_second_signal_for_the_same_trigger_inside_the_window()
    {
        var deduplicator = new TriggerDeduplicator(TimeSpan.FromMilliseconds(500));
        var now = DateTimeOffset.UtcNow;

        var first = deduplicator.Accept(new TriggerSignal(SessionA, "add-item", now));
        var second = deduplicator.Accept(new TriggerSignal(SessionA, "add-item", now.AddMilliseconds(50)));

        Assert.True(first);
        Assert.False(second, "A duplicate UIA invoke event (or a genuine double click) inside the window must collapse into a single logical signal.");
    }

    [Fact]
    public void Accept_accepts_a_second_signal_for_the_same_trigger_outside_the_window()
    {
        var deduplicator = new TriggerDeduplicator(TimeSpan.FromMilliseconds(200));
        var now = DateTimeOffset.UtcNow;

        var first = deduplicator.Accept(new TriggerSignal(SessionA, "add-item", now));
        var second = deduplicator.Accept(new TriggerSignal(SessionA, "add-item", now.AddSeconds(2)));

        Assert.True(first);
        Assert.True(second, "Two genuinely separate clicks, seconds apart, must both be processed.");
    }

    [Fact]
    public void Accept_accepts_signals_for_distinct_triggers_inside_the_same_window()
    {
        var deduplicator = new TriggerDeduplicator(TimeSpan.FromMilliseconds(500));
        var now = DateTimeOffset.UtcNow;

        var addSignal = deduplicator.Accept(new TriggerSignal(SessionA, "add-item", now));
        var finishSignal = deduplicator.Accept(new TriggerSignal(SessionA, "finish-session", now.AddMilliseconds(10)));

        Assert.True(addSignal);
        Assert.True(finishSignal, "Deduplication is per-trigger, not global - two different triggers firing close together must both be processed.");
    }

    [Fact]
    public void Accept_accepts_signals_for_the_same_trigger_in_distinct_sessions_inside_the_same_window()
    {
        var deduplicator = new TriggerDeduplicator(TimeSpan.FromMilliseconds(500));
        var now = DateTimeOffset.UtcNow;

        var sessionASignal = deduplicator.Accept(new TriggerSignal(SessionA, "add-item", now));
        var sessionBSignal = deduplicator.Accept(new TriggerSignal(SessionB, "add-item", now.AddMilliseconds(10)));

        Assert.True(sessionASignal);
        Assert.True(sessionBSignal, "Deduplication keys on session as well as trigger - the same trigger firing in a different session is not a duplicate.");
    }

    [Fact]
    public void Accept_rejects_a_repeated_native_occurrence_id_even_outside_the_time_window()
    {
        var deduplicator = new TriggerDeduplicator(TimeSpan.FromMilliseconds(10));
        var now = DateTimeOffset.UtcNow;

        var first = deduplicator.Accept(new TriggerSignal(SessionA, "add-item", now, NativeOccurrenceId: "evt-1"));
        var second = deduplicator.Accept(new TriggerSignal(SessionA, "add-item", now.AddSeconds(5), NativeOccurrenceId: "evt-1"));

        Assert.True(first);
        Assert.False(second, "The exact same native occurrence identity reported twice must never be treated as two logical signals.");
    }

    [Fact]
    public void Accept_never_considers_payload_value_deciding_duplication()
    {
        // TriggerSignal has no captured-value field at all - this test asserts that
        // contract directly (via reflection) so this deduplication rule cannot silently
        // regress if TriggerSignal is ever extended with one.
        var properties = typeof(TriggerSignal).GetProperties();

        Assert.DoesNotContain(properties, p =>
            p.Name.Contains("Value", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Payload", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Field", StringComparison.OrdinalIgnoreCase));
    }
}
