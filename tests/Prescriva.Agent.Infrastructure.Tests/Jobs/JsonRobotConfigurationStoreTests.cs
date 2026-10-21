using Prescriva.Agent.Application.Jobs;
using Prescriva.Agent.Infrastructure.Jobs;
using Prescriva.Agent.Infrastructure.Security;

namespace Prescriva.Agent.Infrastructure.Tests.Jobs;

/// <summary>The robot's settings at a pharmacy (plan 22); its tokens are never written in clear text.</summary>
public sealed class JsonRobotConfigurationStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "prescriva-robot-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task Settings_are_saved_and_loaded_with_tokens_protected()
    {
        var store = new JsonRobotConfigurationStore(_path, new DpapiPayloadProtector());
        var configuration = new RobotConfiguration(true, RobotLevel.Saves, "19", 8765, "token-local-123", "https://crm.example/", "token-crm-456");

        await store.SaveAsync(configuration, CancellationToken.None);
        var loaded = await new JsonRobotConfigurationStore(_path, new DpapiPayloadProtector()).LoadAsync(CancellationToken.None);

        Assert.Equal(configuration, loaded);
        var text = await File.ReadAllTextAsync(_path);
        Assert.DoesNotContain("token-local-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("token-crm-456", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_file_the_robot_is_off_assisted_with_a_new_local_token()
    {
        var loaded = await new JsonRobotConfigurationStore(_path, new DpapiPayloadProtector()).LoadAsync(CancellationToken.None);

        Assert.False(loaded.Enabled);
        Assert.Equal(RobotLevel.Assisted, loaded.Level);
        Assert.True(loaded.LocalApiToken.Length >= 32);
        Assert.Null(loaded.CrmUrl);
    }
}
