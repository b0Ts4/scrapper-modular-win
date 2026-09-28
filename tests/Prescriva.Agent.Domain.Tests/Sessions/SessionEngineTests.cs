using System.Collections.Immutable;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Domain.Sessions;

namespace Prescriva.Agent.Domain.Tests.Sessions;

public sealed class SessionEngineTests
{
    private static readonly Guid SessionId = Guid.Parse("00000000-0000-0000-0000-000000000010");
    private static readonly Guid FirstId = Guid.Parse("00000000-0000-0000-0000-000000000011");
    private static readonly Guid SecondId = Guid.Parse("00000000-0000-0000-0000-000000000012");
    private static readonly Guid ThirdId = Guid.Parse("00000000-0000-0000-0000-000000000013");
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("add")]
    [InlineData("add-alternative")]
    public void Multiple_triggers_produce_the_same_semantic_action(string triggerId)
    {
        var result = Engine().Apply(Start(), new(triggerId, [FirstId]), Values("Medicine A"), Now);

        Assert.Equal(SessionTransitionStatus.Applied, result.Status);
        var emitted = Assert.Single(result.Events);
        Assert.Equal("item_added", emitted.Type);
        Assert.Equal("Medicine A", emitted.Payload.Fields["name"]);
        Assert.Equal("Medicine A", Assert.Single(result.Session.ConfirmedItems)["name"]);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Advance_and_back_use_the_configured_target_stage()
    {
        var original = Start();
        var next = Engine().Apply(original, new("next", []), Values(), Now);
        var back = Engine().Apply(next.Session, new("back", []), Values(), Now);

        Assert.Equal("review", next.Session.CurrentStageId);
        Assert.Equal("entry", back.Session.CurrentStageId);
        Assert.Equal("entry", original.CurrentStageId);
        Assert.Empty(next.Events);
        Assert.Empty(back.Events);
    }

    [Fact]
    public void Trigger_outside_current_stage_is_ignored_without_reading_fields_or_consuming_ids()
    {
        var original = Start();
        var result = Engine().Apply(original, new("finish", []), Values(), Now);

        Assert.Equal(SessionTransitionStatus.Ignored, result.Status);
        Assert.Same(original, result.Session);
        Assert.Empty(result.Events);
        Assert.Empty(result.Failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Missing_required_value_rejects_the_entire_transition(string? value)
    {
        var original = Start();
        var result = Engine().Apply(original, new("add", [FirstId]), Values(value), Now);

        AssertRejected(result, original, SessionFailureCode.MissingRequiredField);
        Assert.Equal("name", Assert.Single(result.Failures).FieldId);
    }

    [Fact]
    public void Failed_required_capture_does_not_reuse_a_stale_value()
    {
        var engine = Engine();
        var original = engine.Apply(Start(), new("add", [FirstId]), Values("Old"), Now).Session;
        var result = engine.Apply(original, new("add", [SecondId]),
            new Dictionary<string, CapturedFieldValue> { ["name"] = new(null, CaptureFailure.Unreadable) }, Now);

        AssertRejected(result, original, SessionFailureCode.CaptureFailed);
        Assert.Equal(CaptureFailure.Unreadable, Assert.Single(result.Failures).CaptureFailure);
    }

    [Fact]
    public void Optional_missing_capture_removes_stale_value_and_unconfigured_input_is_excluded()
    {
        var engine = Engine();
        var original = engine.Apply(Start(), new("add", [FirstId]),
            new Dictionary<string, CapturedFieldValue> { ["name"] = new("A"), ["note"] = new("Old note") }, Now).Session;
        var result = engine.Apply(original, new("add", [SecondId]),
            new Dictionary<string, CapturedFieldValue> { ["name"] = new("B"), ["secret"] = new("Excluded") }, Now);

        Assert.Equal("B", Assert.Single(result.Events).Payload.Fields["name"]);
        Assert.False(result.Session.Values.ContainsKey("note"));
        Assert.False(result.Session.Values.ContainsKey("secret"));
        Assert.Equal("Old note", original.Values["note"]);
    }

    [Fact]
    public void Events_use_caller_ids_time_configuration_and_monotonically_increasing_sequences()
    {
        var engine = Engine();
        var first = engine.Apply(Start(), new("add", [FirstId]), Values("A"), Now);
        var second = engine.Apply(first.Session, new("add", [SecondId]), Values("B"), Now.AddSeconds(1));
        var firstEvent = Assert.Single(first.Events);
        var secondEvent = Assert.Single(second.Events);

        Assert.Equal(FirstId, firstEvent.Id);
        Assert.Equal(SessionId, firstEvent.SessionId);
        Assert.Equal("budget-flow", firstEvent.ConfigurationId);
        Assert.Equal(1, firstEvent.ConfigurationVersion);
        Assert.Equal(Now, firstEvent.Timestamp);
        Assert.Equal(1, firstEvent.Sequence);
        Assert.Equal(SecondId, secondEvent.Id);
        Assert.Equal(2, secondEvent.Sequence);
        Assert.Equal(Now.AddSeconds(1), secondEvent.Timestamp);
        Assert.Equal(2, second.Session.LastSequence);
        Assert.Equal("A", second.Session.ConfirmedItems[0]["name"]);
        Assert.Equal("B", second.Session.ConfirmedItems[1]["name"]);
        Assert.Equal("A", firstEvent.Payload.Fields["name"]);
        Assert.Single(first.Session.ConfirmedItems);
    }

    [Fact]
    public void Multiple_emits_in_one_trigger_have_distinct_ids_sequences_and_ordered_snapshots()
    {
        var engine = Engine(new("batch", "entry", Selector(), "Invoked",
            [new CaptureFieldsAction(["name"]), new EmitEventAction("first"),
                new TransitionStageAction("review"), new ClearStateAction(), new EmitEventAction("second")]));
        var result = engine.Apply(Start(), new("batch", [FirstId, SecondId]), Values("A"), Now);

        Assert.Equal(2, result.Events.Length);
        Assert.Equal("first", result.Events[0].Type);
        Assert.Equal(FirstId, result.Events[0].Id);
        Assert.Equal(1, result.Events[0].Sequence);
        Assert.Equal("A", result.Events[0].Payload.Fields["name"]);
        Assert.Equal("second", result.Events[1].Type);
        Assert.Equal(SecondId, result.Events[1].Id);
        Assert.Equal(2, result.Events[1].Sequence);
        Assert.Empty(result.Events[1].Payload.Fields);
    }

    [Fact]
    public void Missing_required_field_blocks_an_emit_even_without_a_capture_action()
    {
        var engine = Engine(new("emit", "entry", Selector(), "Invoked", [new EmitEventAction("item_added")]));
        var original = Start();
        AssertRejected(engine.Apply(original, new("emit", [FirstId]), Values("Unrequested"), Now),
            original, SessionFailureCode.MissingRequiredField);
    }

    [Fact]
    public void Failure_after_an_emit_rolls_back_events_stage_values_and_sequence()
    {
        var engine = Engine(new("batch", "entry", Selector(), "Invoked",
            [new CaptureFieldsAction(["name"]), new EmitEventAction("item_added"),
                new TransitionStageAction("review"), new EmitEventAction("reviewed")]));
        var original = Start();

        AssertRejected(engine.Apply(original, new("batch", [FirstId]), Values("A"), Now),
            original, SessionFailureCode.MissingEventId);
    }

    [Fact]
    public void Clear_removes_accumulated_values_and_items_but_preserves_sequence()
    {
        var engine = Engine();
        var original = engine.Apply(Start(), new("add", [FirstId]), Values("A"), Now).Session;
        var cleared = engine.Apply(original, new("clear", []), Values(), Now);
        var added = engine.Apply(cleared.Session, new("add", [SecondId]), Values("B"), Now);

        Assert.Empty(cleared.Session.Values);
        Assert.Empty(cleared.Session.ConfirmedItems);
        Assert.Equal(SessionState.Active, cleared.Session.State);
        Assert.Equal("entry", cleared.Session.CurrentStageId);
        Assert.Equal(1, cleared.Session.LastSequence);
        Assert.Equal(2, Assert.Single(added.Events).Sequence);
        Assert.Single(original.ConfirmedItems);
    }

    [Fact]
    public void Cancel_clears_state_ends_session_and_stops_following_actions()
    {
        var engine = Engine();
        var original = engine.Apply(Start(), new("add", [FirstId]), Values("A"), Now).Session;
        var result = engine.Apply(original, new("cancel", []), Values(), Now);

        Assert.Equal(SessionState.Cancelled, result.Session.State);
        Assert.Empty(result.Session.Values);
        Assert.Empty(result.Session.ConfirmedItems);
        Assert.Equal(1, result.Session.LastSequence);
        Assert.Empty(result.Events);
        AssertRejected(engine.Apply(result.Session, new("add", [SecondId]), Values("B"), Now),
            result.Session, SessionFailureCode.SessionEnded);
    }

    [Fact]
    public void Finish_emits_budget_finished_with_all_confirmed_items_and_ends_session()
    {
        var engine = Engine();
        var first = engine.Apply(Start(), new("add", [FirstId]), Values("A"), Now).Session;
        var second = engine.Apply(first, new("add", [SecondId]), Values("B"), Now).Session;
        var review = engine.Apply(second, new("next", []), Values(), Now).Session;
        var result = engine.Apply(review, new("finish", [ThirdId]), Values(), Now);

        Assert.Equal(SessionState.Finished, result.Session.State);
        var emitted = Assert.Single(result.Events);
        Assert.Equal("budget_finished", emitted.Type);
        Assert.Equal(3, emitted.Sequence);
        Assert.Equal(ThirdId, emitted.Id);
        Assert.Equal("A", emitted.Payload.Items[0]["name"]);
        Assert.Equal("B", emitted.Payload.Items[1]["name"]);
        AssertRejected(engine.Apply(result.Session, new("finish", [FirstId]), Values(), Now),
            result.Session, SessionFailureCode.SessionEnded);
    }

    [Fact]
    public void Unknown_trigger_is_a_typed_failure()
    {
        var original = Start();
        AssertRejected(Engine().Apply(original, new("unknown", []), Values(), Now), original, SessionFailureCode.UnknownTrigger);
    }

    [Fact]
    public void Missing_event_id_is_a_typed_failure()
    {
        var original = Start();
        AssertRejected(Engine().Apply(original, new("add", []), Values("A"), Now), original, SessionFailureCode.MissingEventId);
    }

    [Fact]
    public void Empty_event_id_is_a_typed_failure()
    {
        var original = Start();
        AssertRejected(Engine().Apply(original, new("add", [Guid.Empty]), Values("A"), Now), original, SessionFailureCode.InvalidEventId);
    }

    [Fact]
    public void Reused_event_id_is_rejected_even_after_clear()
    {
        var engine = Engine();
        var first = engine.Apply(Start(), new("add", [FirstId]), Values("A"), Now).Session;
        var original = engine.Apply(first, new("clear", []), Values(), Now).Session;
        AssertRejected(engine.Apply(original, new("add", [FirstId]), Values("B"), Now), original, SessionFailureCode.DuplicateEventId);
    }

    [Fact]
    public void Duplicate_ids_in_one_occurrence_reject_all_events()
    {
        var engine = Engine(new("batch", "entry", Selector(), "Invoked",
            [new CaptureFieldsAction(["name"]), new EmitEventAction("one"), new EmitEventAction("two")]));
        var original = Start();
        AssertRejected(engine.Apply(original, new("batch", [FirstId, FirstId]), Values("A"), Now),
            original, SessionFailureCode.DuplicateEventId);
    }

    [Fact]
    public void Sequence_exhaustion_is_rejected_instead_of_wrapping()
    {
        var original = Start() with { LastSequence = long.MaxValue };
        AssertRejected(Engine().Apply(original, new("add", [FirstId]), Values("A"), Now), original, SessionFailureCode.SequenceExhausted);
    }

    [Fact]
    public void Sessions_are_independent_and_input_dictionary_changes_cannot_change_events()
    {
        var engine = Engine();
        var values = Values("A");
        var first = engine.Apply(Start(), new("add", [FirstId]), values, Now);
        values["name"] = new("Changed");
        var otherSession = CaptureSession.Start(SecondId, "entry");
        var second = engine.Apply(otherSession, new("add", [ThirdId]), Values("B"), Now);

        Assert.Equal("A", first.Session.Values["name"]);
        Assert.Equal("A", Assert.Single(first.Events).Payload.Fields["name"]);
        Assert.Equal(1, Assert.Single(second.Events).Sequence);
        Assert.Equal(SecondId, second.Events[0].SessionId);
        Assert.Empty(otherSession.Values);
    }

    [Theory]
    [InlineData("empty-id")]
    [InlineData("unknown-stage")]
    [InlineData("negative-sequence")]
    public void Invalid_session_metadata_is_a_typed_failure(string invalidPart)
    {
        var original = invalidPart switch
        {
            "empty-id" => Start() with { Id = Guid.Empty },
            "unknown-stage" => Start() with { CurrentStageId = "unknown" },
            _ => Start() with { LastSequence = -1 }
        };
        AssertRejected(Engine().Apply(original, new("add", [FirstId]), Values("A"), Now),
            original, SessionFailureCode.InvalidSession);
    }

    [Fact]
    public void Unsupported_action_is_rejected_instead_of_silently_skipped()
    {
        var engine = Engine(new("custom", "entry", Selector(), "Invoked", [new UnsupportedAction()]));
        var original = Start();
        AssertRejected(engine.Apply(original, new("custom", []), Values(), Now),
            original, SessionFailureCode.UnsupportedAction);
    }

    private sealed record UnsupportedAction : TriggerActionDefinition;

    private static void AssertRejected(SessionTransitionResult result, CaptureSession original, SessionFailureCode code)
    {
        Assert.Equal(SessionTransitionStatus.Rejected, result.Status);
        Assert.Same(original, result.Session);
        Assert.Empty(result.Events);
        Assert.Equal(code, Assert.Single(result.Failures).Code);
    }

    private static CaptureSession Start() => CaptureSession.Start(SessionId, "entry");

    private static Dictionary<string, CapturedFieldValue> Values(string? name = null) =>
        name is null ? [] : new() { ["name"] = new(name) };

    private static ElementFingerprint Selector() => new("erp.exe", "Budget", AutomationId: "control");

    private static SessionEngine Engine(TriggerDefinition? extra = null)
    {
        ImmutableArray<TriggerActionDefinition> add =
            [new CaptureFieldsAction(["name", "note"]), new EmitEventAction("item_added")];
        ImmutableArray<TriggerDefinition> triggers =
        [
            new("add", "entry", Selector(), "Invoked", add),
            new("add-alternative", "entry", Selector(), "Invoked", add),
            new("next", "entry", Selector(), "Invoked", [new TransitionStageAction("review")]),
            new("back", "review", Selector(), "Invoked", [new TransitionStageAction("entry")]),
            new("clear", "entry", Selector(), "Invoked", [new ClearStateAction()]),
            new("cancel", "entry", Selector(), "Invoked", [new CancelSessionAction(), new EmitEventAction("unexpected")]),
            new("finish", "review", Selector(), "Invoked", [new FinishSessionAction(), new CancelSessionAction()])
        ];
        var configuration = new IntegrationConfiguration(1, "budget-flow", "Budget", new("erp.exe", "Budget"),
            [new("name", "entry", "Item name", true, Selector()), new("note", "entry", "Note", false, Selector())],
            [new("entry", "Entry"), new("review", "Review")], extra is null ? triggers : triggers.Add(extra));
        Assert.True(ConfigurationValidator.Validate(configuration).IsValid);
        return new(configuration);
    }
}
