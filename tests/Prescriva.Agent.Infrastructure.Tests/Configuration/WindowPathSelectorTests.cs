using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Tests.Configuration;

/// <summary>
/// Window-path selectors (plan 14, Delphi controls) are part of a configuration's content:
/// they are saved and reloaded, and they change the content hash approvals are bound to.
/// Selectors without a path keep their hash (see <see cref="FieldKindTests"/>).
/// </summary>
public sealed class WindowPathSelectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-window-path-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void A_different_window_path_changes_the_content_hash()
    {
        var first = WithFieldPath("TPanel[0]/TEdit[0]");
        var second = WithFieldPath("TPanel[0]/TEdit[1]");

        Assert.NotEqual(ConfigurationFingerprint.Compute(first), ConfigurationFingerprint.Compute(second));
    }

    [Fact]
    public async Task A_window_path_round_trips_through_the_json_store()
    {
        var store = new JsonConfigurationStore(_directory);
        var configuration = WithFieldPath("TPanel[0]/TEdit[1]");

        await store.SaveAsync(configuration, CancellationToken.None);
        var loaded = await store.LoadAsync("delphi-flow", CancellationToken.None);

        Assert.Equal("TPanel[0]/TEdit[1]", loaded.Fields[0].Selector.WindowPath);
        Assert.Equal(ConfigurationFingerprint.Compute(configuration), ConfigurationFingerprint.Compute(loaded));
    }

    private static IntegrationConfiguration WithFieldPath(string windowPath) => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "delphi-flow",
        "Delphi flow",
        new ApplicationDefinition("erp.exe", "Budget"),
        [new FieldDefinition("item_name", "entry", "Item name", true,
            new ElementFingerprint("erp.exe", "Budget", ControlType: "ControlType.Edit", ClassName: "TEdit", FrameworkId: "Win32", WindowPath: windowPath))],
        [new StageDefinition("entry", "Entry")],
        [new TriggerDefinition("add", "entry", new ElementFingerprint("erp.exe", "Budget", AutomationId: "add", ControlType: "Button"), "Invoke",
            [new CaptureFieldsAction(["item_name"]), new EmitEventAction("item_added")])]);
}
