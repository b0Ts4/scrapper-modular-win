using System.Collections.Immutable;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Desktop.Configuration;

/// <summary>The session-ending action a trigger performs last, if any.</summary>
public enum TriggerTerminalAction
{
    None,

    /// <summary>Emits <c>budget_finished</c> and finishes the session (<see cref="FinishSessionAction"/>).</summary>
    Finish,

    /// <summary>Clears accumulated state and cancels the session (<see cref="CancelSessionAction"/>).</summary>
    Cancel,
}

/// <summary>
/// Turns the configurator's trigger form into the ordered declarative action list the
/// session engine executes. The order is fixed so the result is always meaningful:
/// capture (values exist before anything snapshots them) → emit → clear → transition →
/// terminal action (finish/cancel stop the list, so they always run last).
/// </summary>
public static class TriggerActionsBuilder
{
    public static ImmutableArray<TriggerActionDefinition> Build(
        string? captureFieldIds,
        string? emitEventType,
        string? transitionStageId,
        bool clearState,
        TriggerTerminalAction terminal)
    {
        var actions = ImmutableArray.CreateBuilder<TriggerActionDefinition>();

        var fieldIds = (captureFieldIds ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        if (!fieldIds.IsEmpty)
        {
            actions.Add(new CaptureFieldsAction(fieldIds));
        }

        if (!string.IsNullOrWhiteSpace(emitEventType))
        {
            actions.Add(new EmitEventAction(emitEventType.Trim()));
        }

        if (clearState)
        {
            actions.Add(new ClearStateAction());
        }

        if (!string.IsNullOrWhiteSpace(transitionStageId))
        {
            actions.Add(new TransitionStageAction(transitionStageId.Trim()));
        }

        switch (terminal)
        {
            case TriggerTerminalAction.Finish:
                actions.Add(new FinishSessionAction());
                break;
            case TriggerTerminalAction.Cancel:
                actions.Add(new CancelSessionAction());
                break;
        }

        if (actions.Count == 0)
        {
            throw new ArgumentException(
                "O gatilho precisa de pelo menos uma ação: capturar campos, emitir evento, limpar, mudar de etapa, finalizar ou cancelar.");
        }

        return actions.ToImmutable();
    }
}
