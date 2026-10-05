using System.Collections.Immutable;
using System.Text;

namespace Prescriva.Agent.Domain.Selectors;

public sealed class SelectorMatcher
{
    public SelectorMatch Match(
        ElementFingerprint selector,
        IReadOnlyList<ElementCandidate> candidates,
        SelectorWeights weights)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(weights);
        weights.Validate();

        var scored = candidates
            .Where(candidate => Same(selector.ProcessIdentity, candidate.Fingerprint.ProcessIdentity)
                && Same(selector.WindowRule, candidate.Fingerprint.WindowRule))
            .Select(candidate => (candidate, evidence: Score(selector, candidate.Fingerprint, weights)))
            .Select(item => (item.candidate, item.evidence, score: item.evidence.Values.Sum()))
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.candidate.Id, StringComparer.Ordinal)
            .ToArray();

        var available = AvailableWeight(selector, weights);
        if (scored.Length == 0)
        {
            return new SelectorMatch(SelectorMatchStatus.NotFound, null, 0, ImmutableDictionary<string, int>.Empty, available);
        }

        var best = scored[0];
        int? runnerUp = scored.Length > 1 ? scored[1].score : null;
        if (best.score < weights.MinimumScore)
        {
            return new SelectorMatch(SelectorMatchStatus.NotFound, null, best.score, best.evidence, available, runnerUp);
        }

        if (scored.Length > 1 && best.score - scored[1].score < weights.MinimumLead)
        {
            return new SelectorMatch(SelectorMatchStatus.Ambiguous, null, best.score, best.evidence, available, runnerUp);
        }

        return new SelectorMatch(SelectorMatchStatus.Found, best.candidate.Id, best.score, best.evidence, available, runnerUp);
    }

    private static ImmutableDictionary<string, int> Score(
        ElementFingerprint selector,
        ElementFingerprint candidate,
        SelectorWeights weights)
    {
        var evidence = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        AddIfMatched(evidence, "automationId", selector.AutomationId, candidate.AutomationId, weights.AutomationId);
        AddIfMatched(evidence, "controlType", selector.ControlType, candidate.ControlType, weights.ControlType);
        AddIfMatched(evidence, "name", selector.Name, candidate.Name, weights.Name);
        AddIfMatched(evidence, "className", selector.ClassName, candidate.ClassName, weights.ClassName);
        AddIfMatched(evidence, "frameworkId", selector.FrameworkId, candidate.FrameworkId, weights.FrameworkId);

        if (!selector.Ancestors.IsDefaultOrEmpty && !candidate.Ancestors.IsDefaultOrEmpty
            && selector.Ancestors.Any(expected => candidate.Ancestors.Any(actual => SameAncestor(expected, actual))))
        {
            evidence.Add("ancestors", weights.Ancestors);
        }

        if (!selector.NearbyLabels.IsDefaultOrEmpty && !candidate.NearbyLabels.IsDefaultOrEmpty
            && selector.NearbyLabels.Any(expected => candidate.NearbyLabels.Any(actual => Same(expected, actual))))
        {
            evidence.Add("nearbyLabels", weights.NearbyLabels);
        }

        if (selector.RelativeBounds is { } expectedBounds && candidate.RelativeBounds is { } actualBounds
            && Math.Abs(expectedBounds.X - actualBounds.X) <= 0.05
            && Math.Abs(expectedBounds.Y - actualBounds.Y) <= 0.05
            && Math.Abs(expectedBounds.Width - actualBounds.Width) <= 0.05
            && Math.Abs(expectedBounds.Height - actualBounds.Height) <= 0.05)
        {
            evidence.Add("relativeBounds", weights.RelativeBounds);
        }

        return evidence.ToImmutable();
    }

    /// <summary>The total weight of the signals this selector carries - its best possible score.</summary>
    private static int AvailableWeight(ElementFingerprint selector, SelectorWeights weights)
    {
        var total = 0;
        if (!string.IsNullOrWhiteSpace(selector.AutomationId)) total += weights.AutomationId;
        if (!string.IsNullOrWhiteSpace(selector.ControlType)) total += weights.ControlType;
        if (!string.IsNullOrWhiteSpace(selector.Name)) total += weights.Name;
        if (!string.IsNullOrWhiteSpace(selector.ClassName)) total += weights.ClassName;
        if (!string.IsNullOrWhiteSpace(selector.FrameworkId)) total += weights.FrameworkId;
        if (!selector.Ancestors.IsDefaultOrEmpty) total += weights.Ancestors;
        if (!selector.NearbyLabels.IsDefaultOrEmpty) total += weights.NearbyLabels;
        if (selector.RelativeBounds is not null) total += weights.RelativeBounds;
        return total;
    }

    private static void AddIfMatched(
        ImmutableDictionary<string, int>.Builder evidence,
        string key,
        string? expected,
        string? actual,
        int weight)
    {
        if (Same(expected, actual))
        {
            evidence.Add(key, weight);
        }
    }

    private static bool SameAncestor(AncestorFingerprint expected, AncestorFingerprint actual) =>
        (!string.IsNullOrWhiteSpace(expected.AutomationId)
            || !string.IsNullOrWhiteSpace(expected.ControlType)
            || !string.IsNullOrWhiteSpace(expected.Name))
        && (string.IsNullOrWhiteSpace(expected.AutomationId) || Same(expected.AutomationId, actual.AutomationId))
        && (string.IsNullOrWhiteSpace(expected.ControlType) || Same(expected.ControlType, actual.ControlType))
        && (string.IsNullOrWhiteSpace(expected.Name) || Same(expected.Name, actual.Name));

    private static bool Same(string? expected, string? actual) =>
        !string.IsNullOrWhiteSpace(expected)
        && !string.IsNullOrWhiteSpace(actual)
        && string.Equals(
            expected.Trim().Normalize(NormalizationForm.FormC),
            actual.Trim().Normalize(NormalizationForm.FormC),
            StringComparison.OrdinalIgnoreCase);
}
