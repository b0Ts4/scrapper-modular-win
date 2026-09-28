namespace Prescriva.Agent.Domain.Selectors;

public sealed record SelectorWeights
{
    public const int Version = 1;

    public int AutomationId { get; init; } = 40;
    public int ControlType { get; init; } = 25;
    public int Name { get; init; } = 15;
    public int ClassName { get; init; } = 8;
    public int FrameworkId { get; init; } = 6;
    public int Ancestors { get; init; } = 3;
    public int NearbyLabels { get; init; } = 2;
    public int RelativeBounds { get; init; } = 1;
    public int MinimumScore { get; init; } = 55;
    public int MinimumLead { get; init; } = 10;
}
