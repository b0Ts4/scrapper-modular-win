namespace Prescriva.Agent.Domain.Selectors;

public sealed record SelectorWeights
{
    public const int Version = 2;

    public int AutomationId { get; init; } = 40;
    public int ControlType { get; init; } = 15;
    public int Name { get; init; } = 10;
    public int ClassName { get; init; } = 5;
    public int FrameworkId { get; init; } = 2;
    public int Ancestors { get; init; } = 8;
    public int NearbyLabels { get; init; } = 15;
    public int RelativeBounds { get; init; } = 5;
    public int MinimumScore { get; init; } = 40;
    public int MinimumLead { get; init; } = 10;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(AutomationId);
        ArgumentOutOfRangeException.ThrowIfNegative(ControlType);
        ArgumentOutOfRangeException.ThrowIfNegative(Name);
        ArgumentOutOfRangeException.ThrowIfNegative(ClassName);
        ArgumentOutOfRangeException.ThrowIfNegative(FrameworkId);
        ArgumentOutOfRangeException.ThrowIfNegative(Ancestors);
        ArgumentOutOfRangeException.ThrowIfNegative(NearbyLabels);
        ArgumentOutOfRangeException.ThrowIfNegative(RelativeBounds);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumScore, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MinimumScore, 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumLead, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MinimumLead, 100);

        var totalSignalWeight = (long)AutomationId + ControlType + Name + ClassName
            + FrameworkId + Ancestors + NearbyLabels + RelativeBounds;
        if (totalSignalWeight > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSignalWeight), totalSignalWeight,
                "Total signal weight must not exceed 100.");
        }
    }
}
