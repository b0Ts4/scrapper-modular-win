using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Domain.Tests.Configuration;

/// <summary>An approval holds for the exact content it was tested with, under the selector rules it was tested with.</summary>
public sealed class ConfigurationApprovalTests
{
    private static readonly DateTimeOffset ApprovedAt = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_approval_records_the_current_selector_weights_version()
    {
        Assert.Equal(SelectorWeights.Version, new ConfigurationApproval("budget", "ABC", ApprovedAt).SelectorWeightsVersion);
    }

    [Fact]
    public void An_approval_holds_for_its_content_under_the_current_selector_weights()
    {
        Assert.True(new ConfigurationApproval("budget", "ABC", ApprovedAt).IsValidFor("ABC"));
        Assert.False(new ConfigurationApproval("budget", "ABC", ApprovedAt).IsValidFor("ABD"));
    }

    [Fact]
    public void An_approval_tested_under_other_selector_weights_no_longer_holds()
    {
        var older = new ConfigurationApproval("budget", "ABC", ApprovedAt, SelectorWeightsVersion: SelectorWeights.Version - 1);

        Assert.False(older.IsValidFor("ABC"));
    }
}
