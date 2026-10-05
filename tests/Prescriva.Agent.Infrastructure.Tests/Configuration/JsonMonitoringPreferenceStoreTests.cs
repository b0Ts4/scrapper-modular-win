using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Tests.Configuration;

/// <summary>Which integration the operator left active survives a restart; an unreadable file means "none".</summary>
public sealed class JsonMonitoringPreferenceStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-preference-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task The_active_integration_is_read_back_by_a_new_store_instance_and_can_be_cleared()
    {
        await new JsonMonitoringPreferenceStore(_directory).SetActiveConfigurationIdAsync("budget", CancellationToken.None);
        Assert.Equal("budget", await new JsonMonitoringPreferenceStore(_directory).GetActiveConfigurationIdAsync(CancellationToken.None));

        await new JsonMonitoringPreferenceStore(_directory).SetActiveConfigurationIdAsync(null, CancellationToken.None);
        Assert.Null(await new JsonMonitoringPreferenceStore(_directory).GetActiveConfigurationIdAsync(CancellationToken.None));
    }

    [Fact]
    public async Task No_file_means_no_active_integration()
    {
        Assert.Null(await new JsonMonitoringPreferenceStore(_directory).GetActiveConfigurationIdAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{\"activeConfigurationId\":\"\"}")]
    public async Task A_corrupt_file_means_no_active_integration(string content)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, JsonMonitoringPreferenceStore.FileName), content);

        Assert.Null(await new JsonMonitoringPreferenceStore(_directory).GetActiveConfigurationIdAsync(CancellationToken.None));
    }
}
