using System.IO;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// The rest of the milestone acceptance walkthrough, through the real Desktop UI: the
/// Agent restarted (configuration reloads from disk, approval must be earned again), a
/// required field left empty (rejected visibly, no event), the storage-pressure alert, the
/// monitored application closed mid-session (session ends without an error), explicit local
/// data cleanup, controls moved by a layout change (still found) and duplicated controls
/// (reported ambiguous and blocking approval).
/// </summary>
public sealed class DesktopResilienceWalkthroughTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> LowCapacityThresholds = new Dictionary<string, string>
    {
        ["PRESCRIVA_AGENT_OUTBOX_WARNING"] = "2",
        ["PRESCRIVA_AGENT_OUTBOX_CRITICAL"] = "10",
    };

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-desktop-resilience-" + Guid.NewGuid().ToString("N"));

    public DesktopResilienceWalkthroughTests()
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
    public async Task Restart_rejection_capacity_alert_application_close_cleanup_moved_layout_and_ambiguity()
    {
        var target = TestTargetLauncher.Launch();
        try
        {
            // Configure and save in a first Agent process, then close it.
            using (var first = DesktopProcess.Launch(_dataDirectory, LowCapacityThresholds))
            {
                ArrangeSideBySide(first.Window, target);
                await ConfigureAndSaveMedicineIntegrationAsync(first.Window, target);
                first.Close();
            }

            using var desktop = DesktopProcess.Launch(_dataDirectory, LowCapacityThresholds);
            var agent = desktop.Window;
            ArrangeSideBySide(agent, target);

            // 1. After a restart the configuration reloads from disk, but approval is not
            //    carried over: activation is refused until it is tested again.
            SetText(agent, "IntegrationIdBox", "walkthrough");
            Press(agent, "ReloadButton");
            await WaitForTextAsync(agent, "StatusText", "Integração \'walkthrough\' recarregada com 3 campo(s)");
            Press(agent, "ActivateButton");
            await WaitForTextAsync(agent, "MonitorStatusText", "Ativação recusada");

            SetMedicine(target, "Dipirona-R-1", "500mg-R-1", "10-R-1");
            await RunTestModeAsync(agent, target);
            Press(agent, "ApproveButton");
            await WaitForTextAsync(agent, "TestStatusText", "Configuração aprovada");
            Press(agent, "ActivateButton");
            await WaitUntilAsync(
                () => ListTexts(agent, "DiagnosticsList").Count(text => text.Contains("Monitorando gatilho", StringComparison.Ordinal)) >= 2,
                () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / log: " + ReadTechnicalLog(_dataDirectory));
            Assert.False(IsEnabled(agent, "ClearLocalDataButton"));

            // 2. A required field left empty: visibly rejected, and no event is recorded.
            SetMedicine(target, "Dipirona-R-2", "500mg-R-2", "");
            Press(target.Window, "AddButton");
            await WaitUntilAsync(
                () => ListTexts(agent, "DiagnosticsList").Any(text => text.Contains("o campo obrigatório 'quantity' está vazio", StringComparison.Ordinal)),
                () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")));
            Assert.Empty(ListTexts(agent, "EventsList"));

            // 3. Two valid items reach the (lowered) warning threshold: a visible alert.
            await Task.Delay(800);
            SetMedicine(target, "Dipirona-R-3", "500mg-R-3", "12-R-3");
            Press(target.Window, "AddButton");
            await WaitForEventsAsync(agent, _dataDirectory, 1);
            Assert.Equal(string.Empty, SafeText(agent, "CapacityAlertText"));
            await Task.Delay(800);
            SetMedicine(target, "Amoxicilina-R-4", "875mg-R-4", "21-R-4");
            Press(target.Window, "AddButton");
            await WaitForEventsAsync(agent, _dataDirectory, 2);
            await WaitForTextAsync(agent, "CapacityAlertText", "2 eventos pendentes");

            // 4. The monitored application closes mid-session: the session ends, with no error.
            target.Dispose();
            await WaitUntilAsync(
                () => ListTexts(agent, "DiagnosticsList").Any(text => text.Contains("sessão encerrada", StringComparison.Ordinal)),
                () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")));
            await Task.Delay(TimeSpan.FromSeconds(3)); // longer than the trigger retry delay
            Assert.DoesNotContain(ListTexts(agent, "DiagnosticsList"), text => text.Contains("não está sendo monitorado", StringComparison.Ordinal));
            Assert.DoesNotContain("TriggerWatchFailed", ReadTechnicalLog(_dataDirectory), StringComparison.Ordinal);

            // 5. Explicit, confirmed cleanup of local data once monitoring is stopped.
            Press(agent, "StopMonitoringButton");
            await WaitForTextAsync(agent, "MonitorStatusText", "Monitoramento parado");
            Assert.True(IsEnabled(agent, "ClearLocalDataButton"));
            Press(agent, "ClearLocalDataButton");
            ConfirmMessageBox(desktop, "Limpar dados locais");
            await WaitForTextAsync(agent, "MonitorStatusText", "Dados locais apagados");
            await WaitUntilAsync(() => ListTexts(agent, "EventsList").Length == 0, () => string.Join(" | ", ListTexts(agent, "EventsList")));
            Assert.Equal(string.Empty, SafeText(agent, "CapacityAlertText"));
            SqliteConnection.ClearAllPools();
            var outbox = new SqliteEventOutbox(Path.Combine(_dataDirectory, "events.db"), new DpapiPayloadProtector());
            Assert.Empty(await outbox.ReadPendingAsync(CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(_dataDirectory, "configurations", "walkthrough.json")), "Cleanup must keep configurations.");

            // 6. Controls moved by a layout change are still found by the saved selectors.
            target = TestTargetLauncher.Launch("alternate");
            ArrangeSideBySide(agent, target);
            SetMedicine(target, "Dipirona-R-5", "500mg-R-5", "5-R-5");
            await RunTestModeAsync(agent, target);
            await WaitForTextAsync(agent, "TestStatusText", "todos os campos e gatilhos passaram");
            Assert.True(IsEnabled(agent, "ApproveButton"));
            target.Dispose();

            // 7. Duplicated controls: the field is reported ambiguous and approval is blocked.
            target = TestTargetLauncher.Launch("duplicate-controls");
            ArrangeSideBySide(agent, target);
            await RunTestModeAsync(agent, target);
            await WaitForTextAsync(agent, "TestStatusText", "falharam");
            Assert.Contains(AllTexts(agent), text => text.Contains("Seleção ambígua", StringComparison.Ordinal));
            Assert.False(IsEnabled(agent, "ApproveButton"));
        }
        finally
        {
            target.Dispose();
        }
    }

    /// <summary>Text of an element that may be collapsed (absent from the UIA tree): empty when absent.</summary>
    private static string SafeText(AutomationElement root, string automationId)
    {
        var element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        return element?.Current.Name ?? string.Empty;
    }

    /// <summary>Presses "Yes" (IDYES = 6) on the Agent's confirmation dialog with the given title.</summary>
    private static void ConfirmMessageBox(DesktopProcess desktop, string title)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var dialog = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, desktop.ProcessId),
                    new PropertyCondition(AutomationElement.NameProperty, title)))
                ?? desktop.Window.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, title));
            var yes = dialog?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "6"));
            if (yes is not null)
            {
                ((InvokePattern)yes.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                return;
            }

            Thread.Sleep(150);
        }

        throw new TimeoutException($"Confirmation dialog '{title}' did not appear.");
    }
}
