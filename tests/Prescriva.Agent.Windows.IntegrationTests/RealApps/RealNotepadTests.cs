using System.Windows.Automation;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.RealApps;

/// <summary>
/// The real Windows Notepad: its text area is found by AutomationId (classic Notepad: "15") and
/// the text the test typed into it is read back. Skipped, with the reason, where Notepad is absent
/// or its text area has no AutomationId.
/// </summary>
public sealed class RealNotepadTests
{
    private const string ClassicDocumentId = "15";
    private const int WmSetText = 0x000C;

    [SkippableFact]
    public async Task The_text_area_is_found_by_AutomationId_and_its_text_is_read()
    {
        using var notepad = await RealApp.StartAsync("notepad.exe", "notepad", "Notepad");
        Skip.If(notepad is null, "No Windows Notepad on this machine (notepad.exe did not open a window).");

        var documentId = DocumentAutomationId(notepad!);
        Skip.If(documentId is null, $"Notepad '{notepad!.Window.ProcessName}' exposes no text area with an AutomationId.");
        var document = notepad!.Find(documentId!);
        if (document.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && !((ValuePattern)pattern).Current.IsReadOnly)
        {
            ((ValuePattern)pattern).SetValue("Receita 123");
        }
        else
        {
            // Classic Notepad's multi-line Edit has no writable ValuePattern: set its text directly.
            var handle = new IntPtr(document.Current.NativeWindowHandle);
            Skip.If(handle == IntPtr.Zero, "This Notepad's text area accepts no text from UI Automation and has no window handle to type into.");
            SendMessage(handle, WmSetText, IntPtr.Zero, "Receita 123");
        }

        using var dispatcher = new AutomationDispatcher();
        using var resolver = new UiAutomationSelectorResolver(dispatcher, processId: notepad.Window.ProcessId);
        var selector = new ElementFingerprint(notepad.Window.ProcessName, notepad.Window.WindowTitle, AutomationId: documentId);
        var resolution = await resolver.ResolveAsync(selector, CancellationToken.None);
        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);

        using var capture = new UiAutomationCaptureProvider(dispatcher);
        var result = await capture.CaptureAsync(resolution.Handle!, new FieldDefinition("texto", "notas", "Texto", true, selector), CancellationToken.None);

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.Equal("Receita 123", result.Value?.Trim());
    }

    private static string? DocumentAutomationId(RealApp notepad)
    {
        if (notepad.Element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, ClassicDocumentId)) is not null)
        {
            return ClassicDocumentId;
        }

        var document = notepad.Element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        var id = document?.Current.AutomationId;
        return string.IsNullOrEmpty(id) ? null : id;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string text);
}
