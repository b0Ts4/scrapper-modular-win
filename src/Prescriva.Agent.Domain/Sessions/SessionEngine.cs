using System.Collections.Immutable;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Events;

namespace Prescriva.Agent.Domain.Sessions;

public sealed class SessionEngine
{
    private readonly IntegrationConfiguration configuration;
    private readonly string? revision;

    /// <param name="revision">Content revision stamped on every emitted event (see <see cref="DomainEvent.ConfigurationRevision"/>).</param>
    public SessionEngine(IntegrationConfiguration configuration, string? revision = null)
    {
        var validation = ConfigurationValidator.Validate(configuration);
        if (!validation.IsValid)
            throw new ConfigurationValidationException(validation.Errors);
        this.configuration = configuration;
        this.revision = revision;
    }

    /// <summary>
    /// Applies actions in order, atomically. A rejection returns the original session and no events.
    /// Capture actions use only the supplied values for their configured fields; failed reads never reuse stale values.
    /// Finish emits budget_finished. Finish and cancel stop the action list.
    /// </summary>
    public SessionTransitionResult Apply(
        CaptureSession session,
        TriggerOccurrence occurrence,
        IReadOnlyDictionary<string, CapturedFieldValue> values,
        DateTimeOffset now)
    {
        SessionTransitionResult Reject(SessionFailure failure) => new(session, [], [failure], SessionTransitionStatus.Rejected);

        if (session is null)
            return new(session!, [], [new(SessionFailureCode.InvalidSession)], SessionTransitionStatus.Rejected);
        if (occurrence is null || values is null || session.Values is null || session.ConfirmedItems.IsDefault)
            return Reject(new(SessionFailureCode.InvalidSession));
        if (session.State != SessionState.Active)
            return Reject(new(SessionFailureCode.SessionEnded));
        if (session.Id == Guid.Empty || session.LastSequence < 0 ||
            !configuration.Stages.Any(stage => stage.Id == session.CurrentStageId))
            return Reject(new(SessionFailureCode.InvalidSession));

        var trigger = configuration.Triggers.FirstOrDefault(candidate => candidate.Id == occurrence.TriggerId);
        if (trigger is null)
            return Reject(new(SessionFailureCode.UnknownTrigger));
        if (trigger.StageId != session.CurrentStageId)
            return new(session, [], [], SessionTransitionStatus.Ignored);

        var current = session;
        var events = ImmutableArray.CreateBuilder<DomainEvent>();

        SessionFailure? Emit(string type)
        {
            foreach (var field in configuration.Fields.Where(field => field.Required))
                if (!current.Values.TryGetValue(field.Id, out var value) || string.IsNullOrWhiteSpace(value))
                    return new(SessionFailureCode.MissingRequiredField, field.Id);

            if (occurrence.EventIds.IsDefaultOrEmpty || events.Count >= occurrence.EventIds.Length)
                return new(SessionFailureCode.MissingEventId);
            var id = occurrence.EventIds[events.Count];
            if (id == Guid.Empty)
                return new(SessionFailureCode.InvalidEventId);
            if (current.UsedEventIds.Contains(id))
                return new(SessionFailureCode.DuplicateEventId);
            if (current.LastSequence == long.MaxValue)
                return new(SessionFailureCode.SequenceExhausted);

            current = current with
            {
                LastSequence = current.LastSequence + 1,
                UsedEventIds = current.UsedEventIds.Add(id),
                ConfirmedItems = type == "item_added" ? current.ConfirmedItems.Add(current.Values) : current.ConfirmedItems
            };
            events.Add(new(id, configuration.Id, configuration.SchemaVersion, session.Id,
                current.LastSequence, now, type, new(current.Values, current.ConfirmedItems), revision));
            return null;
        }

        foreach (var action in trigger.Actions)
        {
            switch (action)
            {
                case CaptureFieldsAction capture:
                    foreach (var fieldId in capture.FieldIds)
                    {
                        var field = configuration.Fields.First(candidate => candidate.Id == fieldId);
                        values.TryGetValue(fieldId, out var captured);
                        if (captured?.Failure is { } failure)
                            return Reject(new(SessionFailureCode.CaptureFailed, fieldId, failure));

                        if (string.IsNullOrWhiteSpace(captured?.Value))
                        {
                            if (field.Required)
                                return Reject(new(SessionFailureCode.MissingRequiredField, fieldId));
                            current = current with { Values = current.Values.Remove(fieldId) };
                        }
                        else
                        {
                            current = current with { Values = current.Values.SetItem(fieldId, captured.Value) };
                        }
                    }
                    break;
                case TransitionStageAction transition:
                    current = current with { CurrentStageId = transition.StageId };
                    break;
                case EmitEventAction emit:
                    if (Emit(emit.EventType) is { } emitFailure)
                        return Reject(emitFailure);
                    break;
                case ClearStateAction:
                    current = Clear(current);
                    break;
                case FinishSessionAction:
                    if (Emit("budget_finished") is { } finishFailure)
                        return Reject(finishFailure);
                    current = current with { State = SessionState.Finished };
                    break;
                case CancelSessionAction:
                    current = Clear(current) with { State = SessionState.Cancelled };
                    break;
                default:
                    return Reject(new(SessionFailureCode.UnsupportedAction));
            }

            if (current.State != SessionState.Active)
                break;
        }

        return new(current, events.ToImmutable(), [], SessionTransitionStatus.Applied);
    }

    private static CaptureSession Clear(CaptureSession session) => session with
    {
        Values = ImmutableDictionary<string, string>.Empty,
        ConfirmedItems = []
    };
}
