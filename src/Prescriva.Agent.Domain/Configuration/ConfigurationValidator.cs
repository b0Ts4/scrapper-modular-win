using System.Collections.Immutable;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Domain.Configuration;

public static class ConfigurationValidator
{
    public static ConfigurationValidationResult Validate(IntegrationConfiguration? configuration)
    {
        var errors = new List<ConfigurationValidationError>();
        void Add(string code, string path, string message) => errors.Add(new(code, path, message));

        if (configuration is null)
        {
            Add("MISSING_CONFIGURATION", "$", "Configuration is required.");
            return new(errors);
        }

        if (configuration.SchemaVersion != IntegrationConfiguration.CurrentSchemaVersion)
            Add("UNSUPPORTED_SCHEMA_VERSION", "schemaVersion", "The configuration schema version is not supported.");
        if (string.IsNullOrWhiteSpace(configuration.Id))
            Add("MISSING_CONFIGURATION_ID", "id", "Configuration ID is required.");
        if (string.IsNullOrWhiteSpace(configuration.Name))
            Add("MISSING_CONFIGURATION_NAME", "name", "Configuration name is required.");

        if (configuration.Application is null ||
            string.IsNullOrWhiteSpace(configuration.Application.ProcessIdentity) ||
            string.IsNullOrWhiteSpace(configuration.Application.WindowRule))
            Add("INVALID_APPLICATION", "application", "Application process and window rules are required.");

        var stages = ValidateIds(configuration.Stages, stage => stage?.Id, "stage", errors);
        var fields = ValidateIds(configuration.Fields, field => field?.Id, "field", errors);
        ValidateIds(configuration.Triggers, trigger => trigger?.Id, "trigger", errors);

        if (!configuration.Stages.IsDefault)
        {
            for (var i = 0; i < configuration.Stages.Length; i++)
                if (configuration.Stages[i] is { } stage && string.IsNullOrWhiteSpace(stage.Name))
                    Add("INVALID_STAGE_NAME", $"stages[{i}].name", "Stage name is required.");
        }

        if (!configuration.Fields.IsDefault)
        {
            for (var i = 0; i < configuration.Fields.Length; i++)
            {
                var field = configuration.Fields[i];
                if (field is null) continue;
                if (!stages.Contains(field.StageId)) Add("UNKNOWN_STAGE_REFERENCE", $"fields[{i}].stageId", "Field stage does not exist.");
                if (string.IsNullOrWhiteSpace(field.Meaning)) Add("INVALID_FIELD_MEANING", $"fields[{i}].meaning", "Field meaning is required.");
                ValidateSelector(field.Selector, $"fields[{i}].selector", Add);
            }
        }

        if (!configuration.Triggers.IsDefault)
        {
            for (var i = 0; i < configuration.Triggers.Length; i++)
            {
                var trigger = configuration.Triggers[i];
                if (trigger is null) continue;
                if (!stages.Contains(trigger.StageId)) Add("UNKNOWN_STAGE_REFERENCE", $"triggers[{i}].stageId", "Trigger stage does not exist.");
                if (string.IsNullOrWhiteSpace(trigger.ObservedEvent)) Add("INVALID_OBSERVED_EVENT", $"triggers[{i}].observedEvent", "Observed event is required.");
                ValidateSelector(trigger.Selector, $"triggers[{i}].selector", Add);
                if (trigger.Actions.IsDefaultOrEmpty)
                {
                    Add("MISSING_TRIGGER_ACTIONS", $"triggers[{i}].actions", "Trigger must have at least one action.");
                    continue;
                }

                for (var j = 0; j < trigger.Actions.Length; j++)
                {
                    var path = $"triggers[{i}].actions[{j}]";
                    switch (trigger.Actions[j])
                    {
                        case CaptureFieldsAction capture when capture.FieldIds.IsDefaultOrEmpty:
                            Add("MISSING_FIELD_REFERENCES", path, "Capture action must reference fields.");
                            break;
                        case CaptureFieldsAction capture:
                            foreach (var fieldId in capture.FieldIds)
                                if (!fields.Contains(fieldId)) Add("UNKNOWN_FIELD_REFERENCE", path, "Capture field does not exist.");
                            break;
                        case TransitionStageAction transition when !stages.Contains(transition.StageId):
                            Add("UNKNOWN_STAGE_REFERENCE", path, "Transition stage does not exist.");
                            break;
                        case EmitEventAction emit when string.IsNullOrWhiteSpace(emit.EventType):
                            Add("INVALID_EVENT_TYPE", path, "Event type is required.");
                            break;
                        case null:
                            Add("INVALID_TRIGGER_ACTION", path, "Trigger action is required.");
                            break;
                    }
                }
            }
        }

        return new(errors);
    }

    private static HashSet<string> ValidateIds<T>(
        ImmutableArray<T> items,
        Func<T, string?> getId,
        string kind,
        List<ConfigurationValidationError> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (items.IsDefaultOrEmpty)
        {
            errors.Add(new($"MISSING_{kind.ToUpperInvariant()}S", kind + "s", $"At least one {kind} is required."));
            return ids;
        }

        for (var i = 0; i < items.Length; i++)
        {
            var id = items[i] is null ? null : getId(items[i]);
            if (string.IsNullOrWhiteSpace(id))
                errors.Add(new($"INVALID_{kind.ToUpperInvariant()}_ID", $"{kind}s[{i}].id", $"{kind} ID is required."));
            else if (!ids.Add(id))
                errors.Add(new($"DUPLICATE_{kind.ToUpperInvariant()}_ID", $"{kind}s[{i}].id", $"{kind} ID is duplicated."));
        }

        return ids;
    }

    private static void ValidateSelector(ElementFingerprint? selector, string path, Action<string, string, string> add)
    {
        if (selector is null || string.IsNullOrWhiteSpace(selector.ProcessIdentity) || string.IsNullOrWhiteSpace(selector.WindowRule))
            add("INVALID_SELECTOR", path, "Selector process and window rules are required.");
    }
}
