using System.Collections.Immutable;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Tests.Selection;

/// <summary>
/// Fragility warnings shown in test mode: a selector that resolves today but is likely to
/// break - or to pick the wrong element - when the application changes.
/// </summary>
public sealed class SelectorFragilityTests
{
    private static readonly ImmutableDictionary<string, int> Strong =
        ImmutableDictionary<string, int>.Empty.Add("automationId", 40).Add("controlType", 15);

    [Fact]
    public void A_selector_matched_by_its_AutomationId_with_a_clear_lead_has_no_warning()
    {
        var warnings = SelectorFragility.Assess(Selector(automationId: "MedicationTextBox"), Found(1.0, Strong, lead: 55));

        Assert.Empty(warnings);
    }

    [Fact]
    public void A_selector_without_AutomationId_is_flagged_even_when_its_label_matches()
    {
        var evidence = ImmutableDictionary<string, int>.Empty.Add("nearbyLabels", 20).Add("controlType", 15);

        var warnings = SelectorFragility.Assess(Selector(label: "Observações:"), Found(1.0, evidence, lead: 35));

        Assert.Equal([SelectorFragilityWarning.MissingAutomationId], warnings.ToArray());
    }

    [Fact]
    public void A_match_below_80_percent_of_its_own_signals_is_flagged()
    {
        var warnings = SelectorFragility.Assess(Selector(automationId: "MedicationTextBox"), Found(0.7, Strong, lead: 55));

        Assert.Contains(SelectorFragilityWarning.LowConfidence, warnings);
    }

    [Fact]
    public void A_lead_under_twice_the_required_minimum_is_flagged()
    {
        var weights = new SelectorWeights();

        var warnings = SelectorFragility.Assess(Selector(automationId: "MedicationTextBox"), Found(1.0, Strong, lead: 2 * weights.MinimumLead - 1), weights);
        var clear = SelectorFragility.Assess(Selector(automationId: "MedicationTextBox"), Found(1.0, Strong, lead: 2 * weights.MinimumLead), weights);

        Assert.Contains(SelectorFragilityWarning.NarrowLead, warnings);
        Assert.DoesNotContain(SelectorFragilityWarning.NarrowLead, clear);
    }

    [Fact]
    public void A_match_without_identifier_label_or_name_depends_on_position_and_structure()
    {
        var evidence = ImmutableDictionary<string, int>.Empty.Add("controlType", 15).Add("ancestors", 6).Add("relativeBounds", 5).Add("className", 5);

        var warnings = SelectorFragility.Assess(Selector(), Found(1.0, evidence, lead: 30));

        Assert.Equal([SelectorFragilityWarning.MissingAutomationId, SelectorFragilityWarning.PositionDependent], warnings.ToArray());
    }

    [Fact]
    public void Only_a_found_resolution_is_assessed()
    {
        Assert.Empty(SelectorFragility.Assess(Selector(), SelectorResolution.NotFound("gone")));
    }

    private static ElementFingerprint Selector(string? automationId = null, string? label = null) => new(
        "erp.exe", "Main", AutomationId: automationId, ControlType: "ControlType.Edit",
        NearbyLabels: label is null ? default : [label]);

    private static SelectorResolution Found(double confidence, ImmutableDictionary<string, int> evidence, int? lead) =>
        SelectorResolution.Found(Handle.Instance, confidence, evidence, lead);

    private sealed class Handle : ResolvedElementHandle
    {
        public static readonly Handle Instance = new();
    }
}
