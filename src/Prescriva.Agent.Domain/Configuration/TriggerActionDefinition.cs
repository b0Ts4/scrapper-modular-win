using System.Collections.Immutable;

namespace Prescriva.Agent.Domain.Configuration;

public abstract record TriggerActionDefinition;

public sealed record CaptureFieldsAction(ImmutableArray<string> FieldIds) : TriggerActionDefinition;

public sealed record TransitionStageAction(string StageId) : TriggerActionDefinition;

public sealed record EmitEventAction(string EventType) : TriggerActionDefinition;

public sealed record ClearStateAction : TriggerActionDefinition;

public sealed record FinishSessionAction : TriggerActionDefinition;

public sealed record CancelSessionAction : TriggerActionDefinition;
