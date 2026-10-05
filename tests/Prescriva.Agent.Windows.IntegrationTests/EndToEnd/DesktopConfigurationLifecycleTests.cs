using System.IO;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// Configuration lifecycle through the real Desktop UI: an approval survives an Agent
/// restart (activation without retesting), any edit withdraws it, undoing the edit restores
/// it, edits that would break references are refused, and removing a trigger requires a new
/// test before activation.
/// </summary>
public sealed class DesktopConfigurationLifecycleTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-desktop-lifecycle-" + Guid.NewGuid().ToString("N"));

    public DesktopConfigurationLifecycleTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Approval_survives_a_restart_and_any_edit_requires_a_new_test()
    {
        using var target = TestTargetLauncher.Launch();

        // Configure, save, test and approve in a first Agent process.
        using (var first = DesktopProcess.Launch(_dataDirectory))
        {
            ArrangeSideBySide(first.Window, target);
            await ConfigureAndSaveMedicineIntegrationAsync(first.Window, target);
            await WaitForTextAsync(first.Window, "ApprovalStateText", "Não testada");
            SetMedicine(target, "Dipirona-L-1", "500mg-L-1", "10-L-1");
            await RunTestModeAsync(first.Window, target);
            Press(first.Window, "ApproveButton");
            await WaitForTextAsync(first.Window, "ApprovalStateText", "Aprovada em");
            first.Close();
        }

        Assert.True(File.Exists(Path.Combine(_dataDirectory, "approvals", "walkthrough.approval.json")));

        using var desktop = DesktopProcess.Launch(_dataDirectory);
        var agent = desktop.Window;
        ArrangeSideBySide(agent, target);

        // 1. After a restart the reloaded, unchanged configuration is still approved and
        //    activates without retesting.
        SetText(agent, "IntegrationIdBox", "walkthrough");
        Press(agent, "ReloadButton");
        await WaitForTextAsync(agent, "StatusText", "Reloaded 'walkthrough' with 3 field(s)");
        await WaitForTextAsync(agent, "ApprovalStateText", "Aprovada em");
        await ActivateAndWaitForMonitoringAsync(agent);
        SetMedicine(target, "Dipirona-L-2", "500mg-L-2", "12-L-2");
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, _dataDirectory, 1);
        await StopAsync(agent);

        // 2. Any edit (quantity becomes optional) withdraws the approval...
        SetText(agent, "FieldSemanticIdBox", "quantity");
        SetText(agent, "FieldMeaningBox", "quantity");
        SetToggle(agent, "FieldRequiredBox", on: false);
        Press(agent, "UpdateFieldButton");
        await WaitForTextAsync(agent, "StatusText", "Updated field 'quantity'");
        await WaitForTextAsync(agent, "ApprovalStateText", "Alterada desde o último teste");
        Press(agent, "ActivateButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Ativação recusada");

        // 3. ...and undoing it restores the approval for the original content.
        SetToggle(agent, "FieldRequiredBox", on: true);
        Press(agent, "UpdateFieldButton");
        await WaitForTextAsync(agent, "ApprovalStateText", "Aprovada em");
        await ActivateAndWaitForMonitoringAsync(agent);
        await StopAsync(agent);

        // 4. An edit that would leave a dangling reference is refused, naming the trigger.
        SetText(agent, "FieldSemanticIdBox", "medication");
        Press(agent, "RemoveFieldButton");
        await WaitForTextAsync(agent, "StatusText", "Edit refused");
        Assert.Contains("add_item", Text(agent, "StatusText"), StringComparison.Ordinal);
        await WaitForTextAsync(agent, "ApprovalStateText", "Aprovada em");

        // 5. Removing the Finish trigger and saving requires a new test before activation.
        SetText(agent, "TriggerSemanticIdBox", "finish_budget");
        Press(agent, "RemoveTriggerButton");
        await WaitForTextAsync(agent, "StatusText", "Removed trigger 'finish_budget'");
        Press(agent, "SaveButton");
        await WaitForTextAsync(agent, "StatusText", "Saved. Unsaved changes: False");
        await WaitForTextAsync(agent, "ApprovalStateText", "Alterada desde o último teste");
        Press(agent, "ActivateButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Ativação recusada");

        // The edited configuration on disk no longer has the trigger, and reloading keeps it that way.
        Press(agent, "ReloadButton");
        await WaitForTextAsync(agent, "StatusText", "Reloaded 'walkthrough'");
        Assert.DoesNotContain("finish_budget", Text(agent, "ConfigurationSummaryText"), StringComparison.Ordinal);
    }

    private async Task ActivateAndWaitForMonitoringAsync(AutomationElement agent)
    {
        var before = ListTexts(agent, "DiagnosticsList").Count(text => text.Contains("Monitorando gatilho", StringComparison.Ordinal));
        Press(agent, "ActivateButton");
        await WaitUntilAsync(
            () => ListTexts(agent, "DiagnosticsList").Count(text => text.Contains("Monitorando gatilho", StringComparison.Ordinal)) >= before + 2,
            () => Text(agent, "MonitorStatusText") + " / " + string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / log: " + ReadTechnicalLog(_dataDirectory));
    }

    private static async Task StopAsync(AutomationElement agent)
    {
        Press(agent, "StopMonitoringButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Monitoramento parado");
    }

    private static void SetToggle(AutomationElement root, string automationId, bool on)
    {
        var toggle = (TogglePattern)Find(root, automationId).GetCurrentPattern(TogglePattern.Pattern);
        var wanted = on ? ToggleState.On : ToggleState.Off;
        for (var attempt = 0; attempt < 3 && toggle.Current.ToggleState != wanted; attempt++)
        {
            toggle.Toggle();
        }

        Assert.Equal(wanted, toggle.Current.ToggleState);
    }
}
