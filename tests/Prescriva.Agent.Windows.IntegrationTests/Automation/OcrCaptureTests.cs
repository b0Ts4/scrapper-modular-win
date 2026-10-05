using System.Windows.Automation;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// OCR fields against the real TestTarget: text shown only as an image (no text exposed to
/// UI Automation) is read with the offline Windows OCR; a different image gives a different
/// value; a covered control is refused instead of reading another window's text.
/// </summary>
public sealed class OcrCaptureTests
{
    [Fact]
    public async Task Text_shown_only_as_an_image_is_read_by_OCR()
    {
        using var target = TestTargetLauncher.Launch();
        Press(target, "ShowScannedTextButton");
        await Task.Delay(300);

        var result = await CaptureOcrAsync(target);

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.Equal(OcrFieldCapture.ProviderId, result.ProviderId);
        Assert.Equal("DIPIRONA 500 MG", result.Value);
        Assert.Contains(result.Attempts, attempt => attempt.Succeeded && attempt.PatternName.StartsWith("ocr:", StringComparison.Ordinal));
        Assert.Null(result.Attachment);
    }

    [Fact]
    public async Task Another_image_gives_another_value()
    {
        using var target = TestTargetLauncher.Launch();
        Press(target, "ShowScannedTextButton");
        await Task.Delay(300);
        Assert.Equal("DIPIRONA 500 MG", (await CaptureOcrAsync(target)).Value);

        Press(target, "ShowOtherScannedTextButton");
        await Task.Delay(300);

        Assert.Equal("AMOXICILINA 875 MG", (await CaptureOcrAsync(target)).Value);
    }

    [Fact]
    public async Task A_control_covered_by_another_window_is_refused_instead_of_reading_that_window()
    {
        using var target = TestTargetLauncher.Launch();
        Press(target, "ShowScannedTextButton");
        var bounds = Find(target, "ScannedPrescriptionImage").Current.BoundingRectangle;

        using var cover = TestTargetLauncher.Launch();
        ((TransformPattern)cover.Window.GetCurrentPattern(TransformPattern.Pattern)).Move(bounds.X - 50, bounds.Y - 50);
        await Task.Delay(500);

        var result = await CaptureOcrAsync(target);

        Assert.Equal(CaptureOutcome.Obscured, result.Outcome);
        Assert.Null(result.Value);
    }

    private static async Task<CaptureResult> CaptureOcrAsync(TestTargetLauncher target)
    {
        using var dispatcher = new AutomationDispatcher();
        using var resolver = new UiAutomationSelectorResolver(dispatcher, processId: target.Window.Current.ProcessId);
        var fingerprint = new ElementFingerprint("Prescriva.Agent.TestTarget", target.Window.Current.Name, AutomationId: "ScannedPrescriptionImage");
        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);
        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);

        using var capture = new UiAutomationCaptureProvider(dispatcher);
        return await capture.CaptureAsync(
            resolution.Handle!,
            new FieldDefinition("scanned", "budget", "Receita digitalizada", Required: true, Selector: fingerprint, Kind: FieldKind.OcrText),
            CancellationToken.None);
    }

    private static AutomationElement Find(TestTargetLauncher target, string automationId) =>
        target.Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
        ?? throw new InvalidOperationException($"'{automationId}' not found.");

    private static void Press(TestTargetLauncher target, string automationId) =>
        ((InvokePattern)Find(target, automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
}
