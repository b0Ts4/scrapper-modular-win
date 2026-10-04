using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;

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
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

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

        // Side by side, so the cursor over a TestTarget control is never over the Agent.
        Move(target.Window, 0, 0);
        Move(agent, 560, 0);

        // 1. Integration and stage.
        SetText(agent, "IntegrationIdBox", "walkthrough");
        SetText(agent, "IntegrationNameBox", "Walkthrough");
        SetText(agent, "ProcessIdentityBox", "Prescriva.Agent.TestTarget.exe");
        SetText(agent, "WindowRuleBox", "Prescriva Agent Test Target");
        Press(agent, "CreateIntegrationButton");
        SetText(agent, "StageIdBox", "budget");
        SetText(agent, "StageNameBox", "Orçamento");
        Press(agent, "AddStageButton");
        await WaitForTextAsync(agent, "StatusText", "Added stage 'budget'");

        // 2. Fields, selected visually with the real cursor.
        Press(agent, "StartInspectionButton");
        foreach (var (automationId, fieldId) in new[]
                 {
                     ("MedicationTextBox", "medication"),
                     ("ConcentrationTextBox", "concentration"),
                     ("QuantityTextBox", "quantity"),
                 })
        {
            await HoverAndConfirmAsync(agent, target, automationId);
            SetText(agent, "FieldSemanticIdBox", fieldId);
            SetText(agent, "FieldMeaningBox", fieldId);
            Press(agent, "AddFieldButton");
            await WaitForTextAsync(agent, "StatusText", $"Added field '{fieldId}'");
        }

        // 3. Buttons as triggers with their actions.
        await HoverAndConfirmAsync(agent, target, "AddButton");
        SetText(agent, "TriggerSemanticIdBox", "add_item");
        SetText(agent, "TriggerCaptureFieldsBox", "medication, concentration, quantity");
        SetText(agent, "TriggerEmitEventBox", "item_added");
        SelectComboItem(agent, "TriggerTerminalBox", "Nothing");
        Press(agent, "AddTriggerButton");
        await WaitForTextAsync(agent, "StatusText", "Added trigger 'add_item' with 2 action(s)");

        await HoverAndConfirmAsync(agent, target, "FinishButton");
        SetText(agent, "TriggerSemanticIdBox", "finish_budget");
        SetText(agent, "TriggerCaptureFieldsBox", "");
        SetText(agent, "TriggerEmitEventBox", "");
        SelectComboItem(agent, "TriggerTerminalBox", "Finish session (budget_finished)");
        Press(agent, "AddTriggerButton");
        await WaitForTextAsync(agent, "StatusText", "Added trigger 'finish_budget' with 1 action(s)");
        Press(agent, "StopInspectionButton");

        // 4. Save and reload from disk.
        Press(agent, "SaveButton");
        await WaitForTextAsync(agent, "StatusText", "Saved. Unsaved changes: False");
        Assert.True(File.Exists(Path.Combine(_dataDirectory, "configurations", "walkthrough.json")));
        Press(agent, "ReloadButton");
        await WaitForTextAsync(agent, "StatusText", "Reloaded 'walkthrough' with 3 field(s)");

        // 5. Activation is refused before the configuration is tested.
        Press(agent, "ActivateButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Ativação recusada");

        // 6. Test mode against the running TestTarget; the operator presses each button.
        SetMedicine(target, "Dipirona-UI-1", "500mg-UI-1", "10-UI-1");
        Press(agent, "PrepareTestButton");
        await WaitForTextAsync(agent, "StatusText", "Test prepared");
        Press(agent, "RunTestButton");
        await PressUntilAsync(target, ["AddButton", "FinishButton"], () => IsEnabled(agent, "ApproveButton"));
        Press(agent, "ApproveButton");
        await WaitForTextAsync(agent, "TestStatusText", "Configuração aprovada");
        Assert.Contains(AllTexts(agent), text => text == "Valor lido: Dipirona-UI-1");

        // 7. Activate, wait until both buttons are monitored, then enter two medicines and finish.
        Press(agent, "ActivateButton");
        await WaitUntilAsync(
            () => ListTexts(agent, "DiagnosticsList").Count(text => text.Contains("Monitorando gatilho", StringComparison.Ordinal)) >= 2,
            () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / " + Text(agent, "MonitorStatusText") + " / log: " + ReadTechnicalLog());

        SetMedicine(target, "Dipirona-UI-2", "500mg-UI-2", "12-UI-2");
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, 1);
        await Task.Delay(800);
        SetMedicine(target, "Amoxicilina-UI-3", "875mg-UI-3", "21-UI-3");
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, 2);
        await Task.Delay(800);
        Press(target.Window, "FinishButton");
        await WaitForEventsAsync(agent, 3);

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

    /// <summary>The Agent's technical log (IDs, codes, timings only), for failure messages.</summary>
    private string ReadTechnicalLog()
    {
        try
        {
            using var stream = new FileStream(
                Path.Combine(_dataDirectory, "logs", "technical.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException exception)
        {
            return $"(unreadable: {exception.GetType().Name})";
        }
    }

    private static async Task HoverAndConfirmAsync(AutomationElement agent, TestTargetLauncher target, string automationId)
    {
        var rect = Find(target.Window, automationId).Current.BoundingRectangle;
        var x = (int)(rect.X + rect.Width / 2);
        var y = (int)(rect.Y + rect.Height / 2);

        await WaitUntilAsync(
            () =>
            {
                SetCursorPos(x, y);
                return Text(agent, "HoverStateText").Contains($"AutomationId='{automationId}'", StringComparison.Ordinal);
            },
            () => $"hovering ({x},{y}) for {automationId}; Agent shows: {Text(agent, "HoverStateText")}");

        Press(agent, "ConfirmSelectionButton");
        await WaitForTextAsync(agent, "ConfirmedSelectionText", $"AutomationId='{automationId}'");
    }

    private Task WaitForEventsAsync(AutomationElement agent, int count) =>
        WaitUntilAsync(
            () => ListTexts(agent, "EventsList").Length >= count,
            () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / log: " + ReadTechnicalLog());

    private static Task WaitForTextAsync(AutomationElement root, string automationId, string expected) =>
        WaitUntilAsync(
            () => Text(root, automationId).Contains(expected, StringComparison.Ordinal),
            () => $"'{automationId}' shows: {Text(root, automationId)}");

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> describe)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(150);
        }

        Assert.True(condition(), "Timed out: " + describe());
    }

    private static async Task PressUntilAsync(TestTargetLauncher target, string[] automationIds, Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(400);
            foreach (var automationId in automationIds)
            {
                Press(target.Window, automationId);
            }
        }

        Assert.True(done(), "The test-mode run never passed.");
    }

    private static void SetMedicine(TestTargetLauncher target, string medication, string concentration, string quantity)
    {
        SetText(target.Window, "MedicationTextBox", medication);
        SetText(target.Window, "ConcentrationTextBox", concentration);
        SetText(target.Window, "QuantityTextBox", quantity);
    }

    private static AutomationElement Find(AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
        ?? throw new InvalidOperationException($"Element '{automationId}' not found.");

    private static void SetText(AutomationElement root, string automationId, string value) =>
        ((ValuePattern)Find(root, automationId).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    private static void Press(AutomationElement root, string automationId) =>
        ((InvokePattern)Find(root, automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static bool IsEnabled(AutomationElement root, string automationId) =>
        Find(root, automationId).Current.IsEnabled;

    private static string Text(AutomationElement root, string automationId) =>
        Find(root, automationId).Current.Name ?? string.Empty;

    private static string[] AllTexts(AutomationElement root) =>
        root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>()
            .Select(element => element.Current.Name ?? string.Empty)
            .ToArray();

    private static string[] ListTexts(AutomationElement root, string automationId) =>
        Find(root, automationId)
            .FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>()
            .Select(item => string.Join(" ", new[] { item.Current.Name }
                .Concat(item.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(child => child.Current.Name))))
            .ToArray();

    private static void SelectComboItem(AutomationElement root, string automationId, string itemName)
    {
        var combo = Find(root, automationId);
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        var item = combo.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, itemName))
            ?? throw new InvalidOperationException($"Combo item '{itemName}' not found.");
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        expand.Collapse();
    }

    private static void Move(AutomationElement window, double x, double y)
    {
        var transform = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);
        transform.Move(x, y);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    /// <summary>Launches the built Prescriva.Agent.Desktop.exe with an isolated data directory.</summary>
    private sealed class DesktopProcess : IDisposable
    {
        private readonly Process _process;

        private DesktopProcess(Process process, AutomationElement window)
        {
            _process = process;
            Window = window;
        }

        public AutomationElement Window { get; }

        public static DesktopProcess Launch(string dataDirectory)
        {
            var testTargetPath = TestTargetLauncher.ResolveExecutablePath();
            var binDirectory = Path.GetDirectoryName(testTargetPath)!;
            var executablePath = Path.GetFullPath(Path.Combine(
                binDirectory, "..", "..", "..", "..", "Prescriva.Agent.Desktop", "bin",
                new DirectoryInfo(binDirectory).Parent!.Name, "net10.0-windows", "Prescriva.Agent.Desktop.exe"));
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("Build Prescriva.Agent.Desktop before running this test.", executablePath);
            }

            var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false };
            startInfo.Environment["PRESCRIVA_AGENT_DATA"] = dataDirectory;
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the Agent.");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                process.Refresh();
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"Prescriva.Agent.Desktop exited early with code {process.ExitCode}.");
                }

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return new DesktopProcess(process, AutomationElement.FromHandle(process.MainWindowHandle));
                }

                Thread.Sleep(100);
            }

            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out waiting for the Prescriva.Agent.Desktop main window.");
        }

        public void Close()
        {
            if (_process.HasExited)
            {
                return;
            }

            ((WindowPattern)Window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            if (!_process.WaitForExit(10_000))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5_000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
