using System.Collections.Immutable;

namespace Prescriva.Agent.Domain.Configuration;

public abstract record TriggerActionDefinition;

public sealed record CaptureFieldsAction(ImmutableArray<string> FieldIds) : TriggerActionDefinition;

public sealed record TransitionStageAction(string StageId) : TriggerActionDefinition;

/// <summary>
/// Emits an event. With <see cref="UniqueBy"/>, no event is emitted while those fields hold the
/// same values as in the last event of this type in the session (e.g. one event per formula of a
/// quote, however many times the program asks its questions).
/// </summary>
public sealed record EmitEventAction(string EventType, ImmutableArray<string>? UniqueBy = null) : TriggerActionDefinition;

public sealed record ClearStateAction : TriggerActionDefinition;

public sealed record FinishSessionAction : TriggerActionDefinition;

public sealed record CancelSessionAction : TriggerActionDefinition;
