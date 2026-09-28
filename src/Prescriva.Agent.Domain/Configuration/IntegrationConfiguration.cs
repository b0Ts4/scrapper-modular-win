using System.Collections.Immutable;

namespace Prescriva.Agent.Domain.Configuration;

public sealed record IntegrationConfiguration(
    int SchemaVersion,
    string Id,
    string Name,
    ApplicationDefinition Application,
    ImmutableArray<FieldDefinition> Fields,
    ImmutableArray<StageDefinition> Stages,
    ImmutableArray<TriggerDefinition> Triggers)
{
    public const int CurrentSchemaVersion = 1;
}
