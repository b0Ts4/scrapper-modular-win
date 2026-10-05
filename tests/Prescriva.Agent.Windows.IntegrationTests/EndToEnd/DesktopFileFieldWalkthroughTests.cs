using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// File fields through the real Desktop UI: a prescription path box (copy of the original
/// file) and an image drop zone (image of the control), selected with the real cursor and
/// marked "File / image", tested, approved, activated; Add persists item_added whose fields
/// reference encrypted attachments that read back byte-for-byte (file) and as the shown
/// image (screen), with neither the content nor the file name in plaintext on disk or in
/// the technical log.
/// </summary>
public sealed class DesktopFileFieldWalkthroughTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-desktop-files-" + Guid.NewGuid().ToString("N"));

    public DesktopFileFieldWalkthroughTests()
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
    public async Task Path_and_image_fields_are_captured_as_encrypted_attachments_of_item_added()
    {
        var pdfPath = Path.Combine(_dataDirectory, "receita-e2e-4f9c.pdf");
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4 CONTEUDO-DA-RECEITA-4f9c ").Concat(RandomNumberGenerator.GetBytes(150_000)).ToArray();
        await File.WriteAllBytesAsync(pdfPath, pdf);

        using var target = TestTargetLauncher.Launch();
        using var desktop = DesktopProcess.Launch(_dataDirectory);
        var agent = desktop.Window;
        ArrangeSideBySide(agent, target);
        Press(target.Window, "ShowSampleImageButton");

        // Configure: one text field and two file fields, selected with the real cursor.
        SetText(agent, "IntegrationIdBox", "files");
        SetText(agent, "IntegrationNameBox", "Files");
        SetText(agent, "ProcessIdentityBox", "Prescriva.Agent.TestTarget.exe");
        SetText(agent, "WindowRuleBox", "Prescriva Agent Test Target");
        Press(agent, "CreateIntegrationButton");
        SetText(agent, "StageIdBox", "budget");
        SetText(agent, "StageNameBox", "Orçamento");
        Press(agent, "AddStageButton");
        await WaitForTextAsync(agent, "StatusText", "Added stage 'budget'");

        Press(agent, "StartInspectionButton");
        await AddFieldAsync(agent, target, "MedicationTextBox", "medication", "Text");
        await AddFieldAsync(agent, target, "PrescriptionFileTextBox", "prescription_file", "File / image (copy of the file, max 10 MB)");
        await AddFieldAsync(agent, target, "PrescriptionImage", "prescription_image", "File / image (copy of the file, max 10 MB)");
        Assert.Contains("prescription_file@budget*[file]", Text(agent, "ConfigurationSummaryText"), StringComparison.Ordinal);

        await HoverAndConfirmAsync(agent, target, "AddButton");
        SetText(agent, "TriggerSemanticIdBox", "add_item");
        SetText(agent, "TriggerCaptureFieldsBox", "medication, prescription_file, prescription_image");
        SetText(agent, "TriggerEmitEventBox", "item_added");
        SelectComboItem(agent, "TriggerTerminalBox", "Nothing");
        Press(agent, "AddTriggerButton");
        await WaitForTextAsync(agent, "StatusText", "Added trigger 'add_item'");
        Press(agent, "StopInspectionButton");
        Press(agent, "SaveButton");
        await WaitForTextAsync(agent, "StatusText", "Saved. Unsaved changes: False");

        // Test mode shows what each file field would capture; approve; activate.
        SetText(target.Window, "MedicationTextBox", "Dipirona-F-1");
        SetText(target.Window, "PrescriptionFileTextBox", pdfPath);
        Press(agent, "PrepareTestButton");
        await WaitForTextAsync(agent, "StatusText", "Test prepared");
        await WaitForTextAsync(agent, "TestStatusText", "Pronto.");
        Press(agent, "RunTestButton");
        await PressUntilAsync(target, ["AddButton"], () => Text(agent, "TestStatusText").StartsWith("Teste concluído", StringComparison.Ordinal));
        var texts = AllTexts(agent);
        Assert.Contains(texts, text => text.StartsWith("Valor lido: arquivo receita-e2e-4f9c.pdf (", StringComparison.Ordinal) && text.EndsWith("cópia do arquivo)", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.StartsWith("Valor lido: arquivo prescription_image.png (", StringComparison.Ordinal) && text.EndsWith("imagem da tela)", StringComparison.Ordinal));
        Press(agent, "ApproveButton");
        await WaitForTextAsync(agent, "ApprovalStateText", "Aprovada em");

        Press(agent, "ActivateButton");
        await WaitUntilAsync(
            () => ListTexts(agent, "DiagnosticsList").Any(text => text.Contains("Monitorando gatilho 'add_item'", StringComparison.Ordinal)),
            () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / log: " + ReadTechnicalLog(_dataDirectory));
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, _dataDirectory, 1);

        var shown = Assert.Single(ListTexts(agent, "EventsList"));
        Assert.Contains("prescription_file=arquivo receita-e2e-4f9c.pdf", shown, StringComparison.Ordinal);
        Assert.Contains("prescription_image=arquivo prescription_image.png", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(AttachmentReference.Prefix, shown, StringComparison.Ordinal);

        Press(agent, "StopMonitoringButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Monitoramento parado");
        desktop.Close();

        // The queue left behind: the event references both attachments, which read back
        // exactly (file) and as the shown image (screen).
        SqliteConnection.ClearAllPools();
        var databasePath = Path.Combine(_dataDirectory, "events.db");
        var protector = new DpapiPayloadProtector();
        var persisted = Assert.Single(await new SqliteEventOutbox(databasePath, protector).ReadPendingAsync(CancellationToken.None));
        var attachments = new SqliteAttachmentStore(databasePath, protector);

        Assert.Equal(pdf, await attachments.ReadAsync(persisted.Payload.Fields["prescription_file"], CancellationToken.None));
        var png = await attachments.ReadAsync(persisted.Payload.Fields["prescription_image"], CancellationToken.None);
        var frame = BitmapDecoder.Create(new MemoryStream(png!), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.InRange(frame.PixelWidth, 150, 170);
        Assert.InRange(frame.PixelHeight, 90, 110);

        SqliteConnection.ClearAllPools();
        var databaseBytes = await File.ReadAllBytesAsync(databasePath);
        Assert.True(databaseBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes("CONTEUDO-DA-RECEITA-4f9c")) < 0, "PDF content stored in plaintext.");
        Assert.True(databaseBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("receita-e2e-4f9c")) < 0, "File name stored in plaintext.");
        var technicalLog = ReadTechnicalLog(_dataDirectory);
        Assert.DoesNotContain("receita-e2e-4f9c", technicalLog, StringComparison.Ordinal);
        Assert.Contains("attachment_stored", technicalLog, StringComparison.Ordinal);
    }

    private static async Task AddFieldAsync(AutomationElement agent, TestTargetLauncher target, string automationId, string fieldId, string kind)
    {
        await HoverAndConfirmAsync(agent, target, automationId);
        SetText(agent, "FieldSemanticIdBox", fieldId);
        SetText(agent, "FieldMeaningBox", fieldId);
        SelectComboItem(agent, "FieldKindBox", kind);
        Press(agent, "AddFieldButton");
        await WaitForTextAsync(agent, "StatusText", $"Added field '{fieldId}'");
    }
}
