using System.IO;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// An OCR field through the real Desktop UI: the "scanned prescription" image (no text
/// exposed to UI Automation) is selected with the real cursor and marked "Text via OCR",
/// tested ("Valor lido: DIPIRONA 500 MG"), approved and activated; Add persists item_added
/// with the recognized text, a different image gives a different value, and the recognized
/// text never reaches the technical log.
/// </summary>
public sealed class DesktopOcrFieldWalkthroughTests : IDisposable
{
    private const string OcrKind = "Texto via OCR (lido da imagem do campo)";

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-desktop-ocr-" + Guid.NewGuid().ToString("N"));

    public DesktopOcrFieldWalkthroughTests()
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
    public async Task Text_shown_only_as_an_image_is_captured_into_item_added_by_OCR()
    {
        using var target = TestTargetLauncher.Launch();
        using var desktop = DesktopProcess.Launch(_dataDirectory);
        var agent = desktop.Window;
        ArrangeSideBySide(agent, target);
        Press(target.Window, "ShowScannedTextButton");

        SetText(agent, "IntegrationIdBox", "ocr");
        SetText(agent, "IntegrationNameBox", "OCR");
        SetText(agent, "ProcessIdentityBox", "Prescriva.Agent.TestTarget.exe");
        SetText(agent, "WindowRuleBox", "Prescriva Agent Test Target");
        Press(agent, "CreateIntegrationButton");
        SetText(agent, "StageIdBox", "budget");
        SetText(agent, "StageNameBox", "Orçamento");
        Press(agent, "AddStageButton");
        await WaitForTextAsync(agent, "StatusText", "Etapa \'budget\' adicionada");

        Press(agent, "StartInspectionButton");
        await HoverAndConfirmAsync(agent, target, "ScannedPrescriptionImage");
        SetText(agent, "FieldSemanticIdBox", "scanned_prescription");
        SetText(agent, "FieldMeaningBox", "Receita digitalizada");
        SelectComboItem(agent, "FieldKindBox", OcrKind);
        Press(agent, "AddFieldButton");
        await WaitForTextAsync(agent, "StatusText", "Campo \'scanned_prescription\' adicionado");
        Assert.Contains("scanned_prescription@budget*[ocr]", Text(agent, "ConfigurationSummaryText"), StringComparison.Ordinal);

        await HoverAndConfirmAsync(agent, target, "AddButton");
        SetText(agent, "TriggerSemanticIdBox", "add_item");
        SetCaptureFields(agent, "scanned_prescription");
        SetText(agent, "TriggerEmitEventBox", "item_added");
        SelectComboItem(agent, "TriggerTerminalBox", "Nada");
        Press(agent, "AddTriggerButton");
        await WaitForTextAsync(agent, "StatusText", "Gatilho \'add_item\' adicionado");
        Press(agent, "StopInspectionButton");
        Press(agent, "SaveButton");
        await WaitForTextAsync(agent, "StatusText", "Salvo. Alterações não salvas: não");

        // Test mode shows the recognized text; approve; activate.
        Press(agent, "PrepareTestButton");
        await WaitForTextAsync(agent, "StatusText", "Teste preparado");
        await WaitForTextAsync(agent, "TestStatusText", "Pronto.");
        Press(agent, "RunTestButton");
        await PressUntilAsync(target, ["AddButton"], () => Text(agent, "TestStatusText").StartsWith("Teste concluído", StringComparison.Ordinal));
        var texts = AllTexts(agent);
        Assert.Contains(texts, text => text == "Valor lido: DIPIRONA 500 MG");
        Assert.Contains(texts, text => text == "Provedor: windows-ocr");
        Press(agent, "ApproveButton");
        await WaitForTextAsync(agent, "ApprovalStateText", "Aprovada em");

        Press(agent, "ActivateButton");
        await WaitUntilAsync(
            () => ListTexts(agent, "DiagnosticsList").Any(text => text.Contains("Monitorando gatilho 'add_item'", StringComparison.Ordinal)),
            () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / log: " + ReadTechnicalLog(_dataDirectory));
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, _dataDirectory, 1);

        await Task.Delay(TimeSpan.FromMilliseconds(800));
        Press(target.Window, "ShowOtherScannedTextButton");
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, _dataDirectory, 2);

        Press(agent, "StopMonitoringButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Monitoramento parado");
        desktop.Close();

        SqliteConnection.ClearAllPools();
        var pending = await new SqliteEventOutbox(Path.Combine(_dataDirectory, "events.db"), new DpapiPayloadProtector())
            .ReadPendingAsync(CancellationToken.None);
        Assert.Equal(["DIPIRONA 500 MG", "AMOXICILINA 875 MG"], pending.Select(e => e.Payload.Fields["scanned_prescription"]));

        var technicalLog = ReadTechnicalLog(_dataDirectory);
        Assert.DoesNotContain("DIPIRONA", technicalLog, StringComparison.Ordinal);
        Assert.DoesNotContain("AMOXICILINA", technicalLog, StringComparison.Ordinal);
    }
}
