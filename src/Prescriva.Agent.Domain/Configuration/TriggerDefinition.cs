using System.Collections.Immutable;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Domain.Configuration;

public sealed record TriggerDefinition(
    string Id,
    string StageId,
    ElementFingerprint Selector,
    string ObservedEvent,
    ImmutableArray<TriggerActionDefinition> Actions);
