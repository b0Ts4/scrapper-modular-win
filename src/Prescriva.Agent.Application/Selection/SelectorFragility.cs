using System.Collections.Immutable;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Selection;

/// <summary>Why a selector that resolves today may break, or pick the wrong element, later.</summary>
public enum SelectorFragilityWarning
{
    /// <summary>The element has no AutomationId; it is found by label, name or structure, which the application may change.</summary>
    MissingAutomationId,

    /// <summary>Less than 80% of the selector's own signals matched.</summary>
    LowConfidence,

    /// <summary>Another element scored close behind; a small layout change could make the selector ambiguous.</summary>
    NarrowLead,

    /// <summary>No identifier, label or name matched: only position and structure did.</summary>
    PositionDependent,
}

/// <summary>Assesses a found selector for the fragility warnings shown in test mode (spec §8).</summary>
public static class SelectorFragility
{
    public const double LowConfidenceThreshold = 0.8;

    public static ImmutableArray<SelectorFragilityWarning> Assess(
        ElementFingerprint selector,
        SelectorResolution resolution,
        SelectorWeights? weights = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Status != SelectorResolutionStatus.Found)
        {
            return [];
        }

        weights ??= new SelectorWeights();
        var warnings = ImmutableArray.CreateBuilder<SelectorFragilityWarning>();
        if (string.IsNullOrWhiteSpace(selector.AutomationId))
        {
            warnings.Add(SelectorFragilityWarning.MissingAutomationId);
        }

        if (resolution.Confidence < LowConfidenceThreshold)
        {
            warnings.Add(SelectorFragilityWarning.LowConfidence);
        }

        if (resolution.Lead is { } lead && lead < 2 * weights.MinimumLead)
        {
            warnings.Add(SelectorFragilityWarning.NarrowLead);
        }

        if (resolution.Evidence is { Count: > 0 } evidence &&
            !evidence.ContainsKey("automationId") && !evidence.ContainsKey("nearbyLabels") && !evidence.ContainsKey("name"))
        {
            warnings.Add(SelectorFragilityWarning.PositionDependent);
        }

        return warnings.ToImmutable();
    }
}
