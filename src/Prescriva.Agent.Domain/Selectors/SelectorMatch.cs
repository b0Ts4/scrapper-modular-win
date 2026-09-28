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
    ImmutableDictionary<string, int> Evidence)
{
    public double Confidence => Score / 100.0;
}
