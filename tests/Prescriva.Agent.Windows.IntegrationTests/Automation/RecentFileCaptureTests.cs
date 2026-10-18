using System.IO;
using System.Windows.Automation;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// A recent-file field (plan 16): while its element (the viewer) is open, the newest file the
/// program wrote is copied whole - the screen is not touched, so a partly visible image does not matter.
/// </summary>
public sealed class RecentFileCaptureTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "prescriva-recent-capture-" + Guid.NewGuid().ToString("N"));

    public RecentFileCaptureTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task The_newest_attachment_file_is_copied_whole_while_the_viewer_is_open()
    {
        await File.WriteAllBytesAsync(Path.Combine(_folder, "anexo_antigo.pdf"), [9, 9]);
        File.SetLastWriteTime(Path.Combine(_folder, "anexo_antigo.pdf"), DateTime.Now.AddMinutes(-3));
        byte[] original = [0x25, 0x50, 0x44, 0x46, 1, 2, 3, 4];
        await File.WriteAllBytesAsync(Path.Combine(_folder, "anexo_20261018_183435722.pdf"), original);

        using var target = TestTargetLauncher.Launch("win32-form");
        using var dispatcher = new AutomationDispatcher();
        using var resolver = new UiAutomationSelectorResolver(dispatcher, processId: target.Window.Current.ProcessId);
        var resolution = await resolver.ResolveAsync(ViewerSelector(target), CancellationToken.None);
        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);

        using var capture = new UiAutomationCaptureProvider(dispatcher);
        var result = await capture.CaptureAsync(resolution.Handle!, Field(), CancellationToken.None);

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.NotNull(result.Attachment);
        Assert.Equal(original, result.Attachment!.Content);
        Assert.Equal("anexo_20261018_183435722.pdf", result.Attachment.FileName);
        Assert.Equal(AttachmentSource.File, result.Attachment.Source);
    }

    [Fact]
    public async Task Without_a_recent_file_the_capture_fails_visibly()
    {
        using var target = TestTargetLauncher.Launch("win32-form");
        using var dispatcher = new AutomationDispatcher();
        using var resolver = new UiAutomationSelectorResolver(dispatcher, processId: target.Window.Current.ProcessId);
        var resolution = await resolver.ResolveAsync(ViewerSelector(target), CancellationToken.None);

        using var capture = new UiAutomationCaptureProvider(dispatcher);
        var result = await capture.CaptureAsync(resolution.Handle!, Field(), CancellationToken.None);

        Assert.Equal(CaptureOutcome.FileUnavailable, result.Outcome);
        Assert.Null(result.Attachment);
    }

    private FieldDefinition Field() =>
        new("anexo", "principal", "Anexo da receita", true, new ElementFingerprint("x", "y"), FieldKind.RecentFile,
            new FileSource(_folder, "anexo_*", 60));

    /// <summary>The form's first edit stands in for the viewer: the field only needs it to be open.</summary>
    private static ElementFingerprint ViewerSelector(TestTargetLauncher target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        AutomationElement? form = null;
        while (form is null && DateTime.UtcNow < deadline)
        {
            form = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, target.Window.Current.ProcessId),
                new PropertyCondition(AutomationElement.NameProperty, "Prescriva Win32 Form")));
            if (form is null) Thread.Sleep(100);
        }

        Assert.NotNull(form);
        return new ElementFingerprint("Prescriva.Agent.TestTarget", "Prescriva Win32 Form", ClassName: "Edit", WindowPath: "TPrescrivaPanel[0]/Edit[0]");
    }
}
