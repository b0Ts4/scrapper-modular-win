using Prescriva.Agent.Desktop.Configuration;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Tests.Configuration;

/// <summary>
/// The configurator's trigger form turns a few plain inputs (fields to capture, event to
/// emit, stage to move to, terminal action) into the ordered declarative action list the
/// session engine executes. Order matters: values are captured before an event snapshots
/// them, and a terminal action always runs last.
/// </summary>
public sealed class TriggerActionsBuilderTests
{
    [Fact]
    public void Add_button_captures_the_listed_fields_then_emits_item_added()
    {
        var actions = TriggerActionsBuilder.Build(
            captureFieldIds: "medication, concentration ,quantity",
            emitEventType: "item_added",
            transitionStageId: "",
            clearState: false,
            terminal: TriggerTerminalAction.None);

        Assert.Collection(
            actions,
            action => Assert.Equal(["medication", "concentration", "quantity"], Assert.IsType<CaptureFieldsAction>(action).FieldIds.ToArray()),
            action => Assert.Equal("item_added", Assert.IsType<EmitEventAction>(action).EventType));
    }

    [Fact]
    public void Finish_button_with_nothing_else_is_a_single_finish_action()
    {
        var actions = TriggerActionsBuilder.Build("", "", "", clearState: false, TriggerTerminalAction.Finish);

        Assert.IsType<FinishSessionAction>(Assert.Single(actions));
    }

    [Fact]
    public void All_options_are_ordered_capture_emit_clear_transition_then_terminal()
    {
        var actions = TriggerActionsBuilder.Build("name", "custom", "review", clearState: true, TriggerTerminalAction.Cancel);

        Assert.Collection(
            actions,
            action => Assert.IsType<CaptureFieldsAction>(action),
            action => Assert.IsType<EmitEventAction>(action),
            action => Assert.IsType<ClearStateAction>(action),
            action => Assert.Equal("review", Assert.IsType<TransitionStageAction>(action).StageId),
            action => Assert.IsType<CancelSessionAction>(action));
    }

    [Fact]
    public void Duplicate_and_blank_field_ids_are_dropped()
    {
        var actions = TriggerActionsBuilder.Build("name,,name, ", "", "", clearState: false, TriggerTerminalAction.None);

        Assert.Equal(["name"], Assert.IsType<CaptureFieldsAction>(Assert.Single(actions)).FieldIds.ToArray());
    }

    [Fact]
    public void A_trigger_that_does_nothing_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            TriggerActionsBuilder.Build(" ", " ", " ", clearState: false, TriggerTerminalAction.None));

        Assert.Contains("ação", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
