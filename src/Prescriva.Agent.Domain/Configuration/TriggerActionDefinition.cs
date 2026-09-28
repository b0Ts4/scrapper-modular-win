using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Prescriva.Agent.Domain.Configuration;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CaptureFieldsAction), "captureFields")]
[JsonDerivedType(typeof(TransitionStageAction), "transitionStage")]
[JsonDerivedType(typeof(EmitEventAction), "emitEvent")]
[JsonDerivedType(typeof(ClearStateAction), "clearState")]
[JsonDerivedType(typeof(FinishSessionAction), "finishSession")]
[JsonDerivedType(typeof(CancelSessionAction), "cancelSession")]
public abstract record TriggerActionDefinition;

public sealed record CaptureFieldsAction(ImmutableArray<string> FieldIds) : TriggerActionDefinition;

public sealed record TransitionStageAction(string StageId) : TriggerActionDefinition;

public sealed record EmitEventAction(string EventType) : TriggerActionDefinition;

public sealed record ClearStateAction : TriggerActionDefinition;

public sealed record FinishSessionAction : TriggerActionDefinition;

public sealed record CancelSessionAction : TriggerActionDefinition;
