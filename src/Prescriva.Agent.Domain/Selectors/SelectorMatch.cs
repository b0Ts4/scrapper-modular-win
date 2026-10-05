using System.Collections.Immutable;

namespace Prescriva.Agent.Domain.Selectors;

public enum SelectorMatchStatus
{
    Found,
    NotFound,
    Ambiguous
}

public sealed record SelectorMatch(
    SelectorMatchStatus Status,
    string? CandidateId,
    int Score,
    ImmutableDictionary<string, int> Evidence,
    int AvailableWeight = 100,
    int? RunnerUpScore = null)
{
    /// <summary>
    /// The share of the selector's own signals that matched: <see cref="Score"/> divided by
    /// <see cref="AvailableWeight"/>, the total weight of the signals the selector actually
    /// carries. A full match of whatever the selector knows is 1.0, however few signals it
    /// has - whether it is good enough to act on is decided separately by the minimum score
    /// and lead.
    /// </summary>
    public double Confidence => AvailableWeight <= 0 ? 0 : Math.Min(1.0, Score / (double)AvailableWeight);
}
