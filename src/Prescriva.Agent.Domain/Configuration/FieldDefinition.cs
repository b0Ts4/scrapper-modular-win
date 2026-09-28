using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Domain.Configuration;

public sealed record FieldDefinition(
    string Id,
    string StageId,
    string Meaning,
    bool Required,
    ElementFingerprint Selector);
