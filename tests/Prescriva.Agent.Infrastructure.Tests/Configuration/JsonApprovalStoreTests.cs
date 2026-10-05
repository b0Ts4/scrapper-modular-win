using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Tests.Configuration;

public sealed class JsonApprovalStoreTests : IDisposable
{
    private static readonly DateTimeOffset ApprovedAt = new(2026, 10, 4, 12, 30, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-approval-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Saved_approval_is_loaded_back_by_a_new_store_instance()
    {
        var approval = new ConfigurationApproval("budget-flow", "ABC123", ApprovedAt);
        await new JsonApprovalStore(_directory).SaveAsync(approval, CancellationToken.None);

        var loaded = await new JsonApprovalStore(_directory).LoadAsync("budget-flow", CancellationToken.None);

        Assert.Equal(approval, loaded);
    }

    [Fact]
    public async Task An_approval_saved_before_selector_weights_were_recorded_loads_under_the_current_weights()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "budget-flow.approval.json"),
            "{\"configurationId\":\"budget-flow\",\"fingerprint\":\"ABC123\",\"approvedAtUtc\":\"2026-10-04T12:30:00+00:00\"}");

        var loaded = await new JsonApprovalStore(_directory).LoadAsync("budget-flow", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(Prescriva.Agent.Domain.Selectors.SelectorWeights.Version, loaded.SelectorWeightsVersion);
        Assert.True(loaded.IsValidFor("ABC123"));
    }

    [Fact]
    public async Task The_selector_weights_version_is_saved_with_the_approval()
    {
        await new JsonApprovalStore(_directory).SaveAsync(new ConfigurationApproval("budget-flow", "ABC123", ApprovedAt), CancellationToken.None);

        var json = await File.ReadAllTextAsync(Path.Combine(_directory, "budget-flow.approval.json"));

        Assert.Contains($"\"selectorWeightsVersion\": {Prescriva.Agent.Domain.Selectors.SelectorWeights.Version}", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_approval_loads_as_null()
    {
        Assert.Null(await new JsonApprovalStore(_directory).LoadAsync("budget-flow", CancellationToken.None));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"configurationId\":\"budget-flow\"}")]
    [InlineData("{\"configurationId\":\"budget-flow\",\"fingerprint\":\"\",\"approvedAtUtc\":\"2026-10-04T12:30:00+00:00\"}")]
    [InlineData("null")]
    public async Task Corrupt_or_incomplete_approval_file_never_approves_anything(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "budget-flow.approval.json"), content);

        Assert.Null(await new JsonApprovalStore(_directory).LoadAsync("budget-flow", CancellationToken.None));
    }

    [Fact]
    public async Task An_approval_file_for_another_configuration_is_ignored()
    {
        var store = new JsonApprovalStore(_directory);
        await store.SaveAsync(new ConfigurationApproval("other-flow", "ABC123", ApprovedAt), CancellationToken.None);
        File.Move(Path.Combine(_directory, "other-flow.approval.json"), Path.Combine(_directory, "budget-flow.approval.json"));

        Assert.Null(await store.LoadAsync("budget-flow", CancellationToken.None));
    }

    [Fact]
    public async Task Saving_again_overwrites_and_delete_removes()
    {
        var store = new JsonApprovalStore(_directory);
        await store.SaveAsync(new ConfigurationApproval("budget-flow", "OLD", ApprovedAt), CancellationToken.None);
        await store.SaveAsync(new ConfigurationApproval("budget-flow", "NEW", ApprovedAt.AddHours(1)), CancellationToken.None);

        Assert.Equal("NEW", (await store.LoadAsync("budget-flow", CancellationToken.None))!.Fingerprint);
        Assert.Single(Directory.GetFiles(_directory));

        await store.DeleteAsync("budget-flow", CancellationToken.None);
        Assert.Null(await store.LoadAsync("budget-flow", CancellationToken.None));
        await store.DeleteAsync("budget-flow", CancellationToken.None); // idempotent
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(" ")]
    public async Task Identifiers_that_are_not_safe_file_names_are_rejected(string id)
    {
        var store = new JsonApprovalStore(_directory);

        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(id, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new ConfigurationApproval(id, "X", ApprovedAt), CancellationToken.None));
    }
}
