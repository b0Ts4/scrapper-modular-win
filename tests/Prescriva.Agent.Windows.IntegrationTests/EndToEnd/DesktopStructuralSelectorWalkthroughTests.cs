using System.IO;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;
using Prescriva.Agent.Windows.IntegrationTests.Automation;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// A field with a visible label but no AutomationId ("Observações:"), selected with the
/// real cursor in the real Desktop UI, is captured into item_added - and still captured
/// after the target application restarts with its fields swapped around, while the
/// monitor reports the integration healthy.
/// </summary>
public sealed class DesktopStructuralSelectorWalkthroughTests : IDisposable
{
    private const string NotesLabel = "Observações:";

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-desktop-structural-" + Guid.NewGuid().ToString("N"));

    public DesktopStructuralSelectorWalkthroughTests()
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
    public async Task A_labelled_field_without_AutomationId_is_captured_before_and_after_a_layout_change()
    {
        var target = TestTargetLauncher.Launch();
        try
        {
            using var desktop = DesktopProcess.Launch(_dataDirectory);
            var agent = desktop.Window;
            ArrangeSideBySide(agent, target);

            // Integration, stage, one AutomationId field and one label-only field.
            SetText(agent, "IntegrationIdBox", "structural");
            SetText(agent, "IntegrationNameBox", "Structural");
            SetText(agent, "ProcessIdentityBox", "Prescriva.Agent.TestTarget.exe");
            SetText(agent, "WindowRuleBox", "Prescriva Agent Test Target");
            Press(agent, "CreateIntegrationButton");
            SetText(agent, "StageIdBox", "budget");
            SetText(agent, "StageNameBox", "Orçamento");
            Press(agent, "AddStageButton");
            await WaitForTextAsync(agent, "StatusText", "Added stage 'budget'");

            Press(agent, "StartInspectionButton");
            await HoverAndConfirmAsync(agent, target, "MedicationTextBox");
            SetText(agent, "FieldSemanticIdBox", "medication");
            SetText(agent, "FieldMeaningBox", "Medicamento");
            Press(agent, "AddFieldButton");
            await WaitForTextAsync(agent, "StatusText", "Added field 'medication'");

            await HoverAndConfirmElementAsync(agent, StructuralSelectorTests.LabelledBox(target, NotesLabel), $"Label='{NotesLabel}'", "hover-notes");
            Assert.Contains("AutomationId=''", Text(agent, "ConfirmedSelectionText"), StringComparison.Ordinal);
            SetText(agent, "FieldSemanticIdBox", "notes");
            SetText(agent, "FieldMeaningBox", "Observações");
            Press(agent, "AddFieldButton");
            await WaitForTextAsync(agent, "StatusText", "Added field 'notes'");

            await HoverAndConfirmAsync(agent, target, "AddButton");
            SetText(agent, "TriggerSemanticIdBox", "add_item");
            SetText(agent, "TriggerCaptureFieldsBox", "medication, notes");
            SetText(agent, "TriggerEmitEventBox", "item_added");
            SelectComboItem(agent, "TriggerTerminalBox", "Nothing");
            Press(agent, "AddTriggerButton");
            await WaitForTextAsync(agent, "StatusText", "Added trigger 'add_item'");
            Press(agent, "StopInspectionButton");
            Press(agent, "SaveButton");
            await WaitForTextAsync(agent, "StatusText", "Saved. Unsaved changes: False");

            // Test mode reads the label-only field; approve and activate.
            SetText(target.Window, "MedicationTextBox", "Dipirona-S-0");
            SetValue(StructuralSelectorTests.LabelledBox(target, NotesLabel), "nota-teste");
            Press(agent, "PrepareTestButton");
            await WaitForTextAsync(agent, "StatusText", "Test prepared");
            await WaitForTextAsync(agent, "TestStatusText", "Pronto.");
            Press(agent, "RunTestButton");
            await PressUntilAsync(target, ["AddButton"], () => Text(agent, "TestStatusText").StartsWith("Teste concluído", StringComparison.Ordinal));
            Assert.Contains(AllTexts(agent), text => text == "Valor lido: nota-teste");
            Press(agent, "ApproveButton");
            await WaitForTextAsync(agent, "ApprovalStateText", "Aprovada em");

            Press(agent, "ActivateButton");
            await WaitForMonitoringAsync(agent, expectedSessions: 1);
            SetText(target.Window, "MedicationTextBox", "Dipirona-S-1");
            SetValue(StructuralSelectorTests.LabelledBox(target, NotesLabel), "nota-original");
            Press(target.Window, "AddButton");
            await WaitForEventsAsync(agent, _dataDirectory, 1);
            await WaitForTextAsync(agent, "HealthText", "Integração saudável");

            // The application restarts with the labelled fields swapped: still captured.
            target.Dispose();
            target = TestTargetLauncher.Launch("alternate");
            ArrangeSideBySide(agent, target);
            await WaitForMonitoringAsync(agent, expectedSessions: 2);
            SetText(target.Window, "MedicationTextBox", "Dipirona-S-2");
            SetValue(StructuralSelectorTests.LabelledBox(target, NotesLabel), "nota-movida");
            SetValue(StructuralSelectorTests.LabelledBox(target, "Lote:"), "lote-nao-capturado");
            Press(target.Window, "AddButton");
            await WaitForEventsAsync(agent, _dataDirectory, 2);
            await WaitForTextAsync(agent, "HealthText", "Integração saudável");

            var shown = ListTexts(agent, "EventsList");
            Assert.Contains(shown, text => text.Contains("notes=nota-original", StringComparison.Ordinal));
            Assert.Contains(shown, text => text.Contains("notes=nota-movida", StringComparison.Ordinal));
            Assert.DoesNotContain(shown, text => text.Contains("lote-nao-capturado", StringComparison.Ordinal));

            Press(agent, "StopMonitoringButton");
            await WaitForTextAsync(agent, "MonitorStatusText", "Monitoramento parado");
            desktop.Close();

            var pending = await new SqliteEventOutbox(Path.Combine(_dataDirectory, "events.db"), new DpapiPayloadProtector())
                .ReadPendingAsync(CancellationToken.None);
            Assert.Equal(["nota-original", "nota-movida"], pending.Select(e => e.Payload.Fields["notes"]));
        }
        finally
        {
            target.Dispose();
        }
    }

    private async Task WaitForMonitoringAsync(AutomationElement agent, int expectedSessions) =>
        await WaitUntilAsync(
            () => ListTexts(agent, "DiagnosticsList").Count(text => text.Contains("Monitorando gatilho 'add_item'", StringComparison.Ordinal)) >= expectedSessions,
            () => Text(agent, "MonitorStatusText") + " / " + string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / log: " + ReadTechnicalLog(_dataDirectory));

    private static void SetValue(AutomationElement element, string value) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
}
