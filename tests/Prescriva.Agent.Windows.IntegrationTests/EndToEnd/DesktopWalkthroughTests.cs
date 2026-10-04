using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;

using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// The milestone walkthrough performed through the real, compiled
/// Prescriva.Agent.Desktop.exe user interface, as an operator would: the real mouse cursor
/// is moved over each TestTarget control while inspection is running (the Agent polls the
/// OS cursor, highlights and identifies the element), the selection is confirmed and given
/// a meaning, stage and actions, the configuration is saved and reloaded, tested against
/// the live TestTarget, approved, activated, and two medicines plus a finish are entered in
/// the TestTarget. The Agent's own buttons and text boxes are driven through UI Automation
/// patterns (Invoke/Value) rather than synthesized clicks, which is what keeps this test
/// deterministic; the pointer-driven inspection path uses the genuine OS cursor.
///
/// Requires an interactive Windows desktop (a cursor that can be positioned).
/// </summary>
public sealed class DesktopWalkthroughTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-desktop-walkthrough-" + Guid.NewGuid().ToString("N"));

    public DesktopWalkthroughTests()
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
    public async Task Operator_configures_tests_activates_and_sees_events_through_the_real_Desktop_UI()
    {
        using var target = TestTargetLauncher.Launch();
        using var desktop = DesktopProcess.Launch(_dataDirectory);
        var agent = desktop.Window;

        ArrangeSideBySide(agent, target);

        // 1-4. Integration, stage, fields and buttons selected with the real cursor; saved.
        await ConfigureAndSaveMedicineIntegrationAsync(agent, target);
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "configurations", "walkthrough.json")));
        Press(agent, "ReloadButton");
        await WaitForTextAsync(agent, "StatusText", "Reloaded 'walkthrough' with 3 field(s)");

        // 5. Activation is refused before the configuration is tested.
        Press(agent, "ActivateButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Ativação recusada");

        // 6. Test mode against the running TestTarget; the operator presses each button.
        SetMedicine(target, "Dipirona-UI-1", "500mg-UI-1", "10-UI-1");
        await RunTestModeAsync(agent, target);
        Assert.True(IsEnabled(agent, "ApproveButton"));
        Press(agent, "ApproveButton");
        await WaitForTextAsync(agent, "TestStatusText", "Configuração aprovada");
        Assert.Contains(AllTexts(agent), text => text == "Valor lido: Dipirona-UI-1");

        // 7. Activate, wait until both buttons are monitored, then enter two medicines and finish.
        Press(agent, "ActivateButton");
        await WaitUntilAsync(
            () => ListTexts(agent, "DiagnosticsList").Count(text => text.Contains("Monitorando gatilho", StringComparison.Ordinal)) >= 2,
            () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / " + Text(agent, "MonitorStatusText") + " / log: " + ReadTechnicalLog(_dataDirectory));

        SetMedicine(target, "Dipirona-UI-2", "500mg-UI-2", "12-UI-2");
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, _dataDirectory, 1);
        await Task.Delay(800);
        SetMedicine(target, "Amoxicilina-UI-3", "875mg-UI-3", "21-UI-3");
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, _dataDirectory, 2);
        await Task.Delay(800);
        Press(target.Window, "FinishButton");
        await WaitForEventsAsync(agent, _dataDirectory, 3);

        var shown = ListTexts(agent, "EventsList");
        Assert.Contains(shown, text => text.Contains("#1 item_added", StringComparison.Ordinal) && text.Contains("medication=Dipirona-UI-2", StringComparison.Ordinal));
        Assert.Contains(shown, text => text.Contains("#2 item_added", StringComparison.Ordinal) && text.Contains("medication=Amoxicilina-UI-3", StringComparison.Ordinal));
        Assert.Contains(shown, text => text.Contains("#3 budget_finished", StringComparison.Ordinal) && text.Contains("2 item(ns)", StringComparison.Ordinal));
        Assert.DoesNotContain(ListTexts(agent, "DiagnosticsList"), text => text.Contains("rejeitado", StringComparison.Ordinal));

        // 8. Close the Agent; the encrypted queue it leaves behind holds the three events.
        Press(agent, "StopMonitoringButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Monitoramento parado");
        desktop.Close();

        var outbox = new SqliteEventOutbox(Path.Combine(_dataDirectory, "events.db"), new DpapiPayloadProtector());
        var pending = await outbox.ReadPendingAsync(CancellationToken.None);
        Assert.Equal(["item_added", "item_added", "budget_finished"], pending.Select(e => e.Type));
        Assert.Equal([1L, 2L, 3L], pending.Select(e => e.Sequence));
        Assert.Equal("Amoxicilina-UI-3", pending[2].Payload.Items[1]["medication"]);

        var technicalLog = await File.ReadAllTextAsync(Path.Combine(_dataDirectory, "logs", "technical.jsonl"));
        Assert.DoesNotContain("Dipirona-UI", technicalLog, StringComparison.Ordinal);
        Assert.DoesNotContain("Amoxicilina-UI", technicalLog, StringComparison.Ordinal);
    }
}
