using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Testing;

/// <summary>
/// Computes a deterministic content hash (SHA-256, hex-encoded) of an
/// <see cref="IntegrationConfiguration"/>'s full, meaningful content - schema version, ID,
/// name, application definition, every stage, every field (including its selector
/// fingerprint), and every trigger (including its selector fingerprint, observed event and
/// actions). Two calls against configurations with identical content always produce the
/// identical fingerprint; any content change at all - even one that looks
/// "whitespace-irrelevant" - produces a different one.
///
/// This is what <see cref="ConfigurationApproval.IsValidFor"/> is checked against: binding
/// an approval to this fingerprint rather than to a configuration's stable ID means that
/// editing a previously-tested configuration in any way invalidates its approval, forcing
/// it to be re-tested before it can activate again. "Somente configurações testadas podem
/// ser ativadas" only holds if this hash is genuinely sensitive to every field that could
/// change what actually happens at runtime - so every property reachable from
/// <see cref="IntegrationConfiguration"/> is folded in below, deliberately, rather than a
/// convenient subset.
///
/// Walks the configuration's own shape explicitly instead of using a general-purpose
/// serializer: <see cref="TriggerActionDefinition"/> is a closed polymorphic hierarchy that
/// a naive reflection-based serializer would not round-trip faithfully (it would only ever
/// see the abstract base type's - empty - own properties), so each concrete action type is
/// matched and folded in by hand below.
/// </summary>
public static class ConfigurationFingerprint
{
    public static string Compute(IntegrationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var builder = new StringBuilder();
        AppendConfiguration(builder, configuration);

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static void AppendConfiguration(StringBuilder sb, IntegrationConfiguration configuration)
    {
        sb.Append("schemaVersion=").Append(configuration.SchemaVersion).Append(';');
        sb.Append("id=").Append(configuration.Id).Append(';');
        sb.Append("name=").Append(configuration.Name).Append(';');
        sb.Append("application.processIdentity=").Append(configuration.Application.ProcessIdentity).Append(';');
        sb.Append("application.windowRule=").Append(configuration.Application.WindowRule).Append(';');

        sb.Append("stages=[");
        if (!configuration.Stages.IsDefault)
        {
            foreach (var stage in configuration.Stages)
            {
                sb.Append('{').Append("id=").Append(stage?.Id).Append(",name=").Append(stage?.Name).Append('}');
            }
        }

        sb.Append("];fields=[");
        if (!configuration.Fields.IsDefault)
        {
            foreach (var field in configuration.Fields)
            {
                sb.Append('{')
                    .Append("id=").Append(field?.Id)
                    .Append(",stageId=").Append(field?.StageId)
                    .Append(",meaning=").Append(field?.Meaning)
                    .Append(",required=").Append(field?.Required);

                // Appended only for non-text fields, so every configuration written before
                // field kinds existed keeps the exact hash its approval is bound to.
                if (field is not null && field.Kind != FieldKind.Text)
                {
                    sb.Append(",kind=").Append(field.Kind);
                }

                sb
                    .Append(",selector=");
                AppendSelector(sb, field?.Selector);
                sb.Append('}');
            }
        }

        sb.Append("];triggers=[");
        if (!configuration.Triggers.IsDefault)
        {
            foreach (var trigger in configuration.Triggers)
            {
                sb.Append('{')
                    .Append("id=").Append(trigger?.Id)
                    .Append(",stageId=").Append(trigger?.StageId)
                    .Append(",selector=");
                AppendSelector(sb, trigger?.Selector);
                sb.Append(",observedEvent=").Append(trigger?.ObservedEvent).Append(",actions=[");
                if (trigger is not null && !trigger.Actions.IsDefault)
                {
                    foreach (var action in trigger.Actions)
                    {
                        AppendAction(sb, action);
                    }
                }

                sb.Append("]}");
            }
        }

        sb.Append(']');
    }

    private static void AppendSelector(StringBuilder sb, ElementFingerprint? selector)
    {
        if (selector is null)
        {
            sb.Append("(null)");
            return;
        }

        sb.Append('(')
            .Append("processIdentity=").Append(selector.ProcessIdentity)
            .Append(",windowRule=").Append(selector.WindowRule)
            .Append(",automationId=").Append(selector.AutomationId)
            .Append(",name=").Append(selector.Name)
            .Append(",controlType=").Append(selector.ControlType)
            .Append(",className=").Append(selector.ClassName)
            .Append(",frameworkId=").Append(selector.FrameworkId)
            .Append(",ancestors=[");

        if (!selector.Ancestors.IsDefault)
        {
            foreach (var ancestor in selector.Ancestors)
            {
                sb.Append('<')
                    .Append(ancestor.AutomationId).Append(',')
                    .Append(ancestor.ControlType).Append(',')
                    .Append(ancestor.Name)
                    .Append('>');
            }
        }

        sb.Append("],nearbyLabels=[");
        if (!selector.NearbyLabels.IsDefault)
        {
            foreach (var label in selector.NearbyLabels)
            {
                sb.Append('<').Append(label).Append('>');
            }
        }

        sb.Append("],relativeBounds=");
        if (selector.RelativeBounds is { } bounds)
        {
            sb.Append(bounds.X.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(bounds.Y.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(bounds.Width.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(bounds.Height.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append("(null)");
        }

        sb.Append(')');
    }

    private static void AppendAction(StringBuilder sb, TriggerActionDefinition? action)
    {
        sb.Append('{');
        switch (action)
        {
            case CaptureFieldsAction capture:
                sb.Append("CaptureFields:");
                if (!capture.FieldIds.IsDefault)
                {
                    foreach (var fieldId in capture.FieldIds)
                    {
                        sb.Append(fieldId).Append(',');
                    }
                }

                break;

            case TransitionStageAction transition:
                sb.Append("TransitionStage:").Append(transition.StageId);
                break;

            case EmitEventAction emit:
                sb.Append("EmitEvent:").Append(emit.EventType);
                break;

            case ClearStateAction:
                sb.Append("ClearState");
                break;

            case FinishSessionAction:
                sb.Append("FinishSession");
                break;

            case CancelSessionAction:
                sb.Append("CancelSession");
                break;

            default:
                sb.Append("Unknown:").Append(action?.GetType().Name ?? "null");
                break;
        }

        sb.Append('}');
    }
}
