using System.IO;
using System.Security.Cryptography;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// File fields against the real TestTarget: a path box yields an exact copy of the file
/// (bounded at 10 MB, typed failure for a missing file); an image-only control yields a PNG
/// of exactly that control - refused when another window covers it.
/// </summary>
public sealed class FileCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-file-capture-" + Guid.NewGuid().ToString("N"));

    public FileCaptureTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_path_box_yields_an_exact_copy_of_the_original_file()
    {
        var path = Path.Combine(_directory, "receita-123.pdf");
        var content = RandomNumberGenerator.GetBytes(200_000);
        await File.WriteAllBytesAsync(path, content);
        using var target = TestTargetLauncher.Launch();
        SetText(target, "PrescriptionFileTextBox", path);

        var result = await CaptureFileAsync(target, "PrescriptionFileTextBox");

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        var attachment = Assert.IsType<CapturedAttachment>(result.Attachment);
        Assert.Equal(content, attachment.Content);
        Assert.Equal("receita-123.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(AttachmentSource.File, attachment.Source);
    }

    [Fact]
    public async Task A_file_over_10_MB_is_refused_without_reading_it()
    {
        var path = Path.Combine(_directory, "grande.jpg");
        await using (var stream = File.Create(path))
        {
            stream.SetLength(AttachmentLimits.MaxBytes + 1);
        }

        using var target = TestTargetLauncher.Launch();
        SetText(target, "PrescriptionFileTextBox", path);

        var result = await CaptureFileAsync(target, "PrescriptionFileTextBox");

        Assert.Equal(CaptureOutcome.TooLarge, result.Outcome);
        Assert.Null(result.Attachment);
    }

    [Fact]
    public async Task A_path_that_does_not_exist_is_a_typed_failure()
    {
        using var target = TestTargetLauncher.Launch();
        SetText(target, "PrescriptionFileTextBox", Path.Combine(_directory, "nao-existe.png"));

        var result = await CaptureFileAsync(target, "PrescriptionFileTextBox");

        Assert.Equal(CaptureOutcome.FileUnavailable, result.Outcome);
        Assert.Null(result.Attachment);
    }

    [Fact]
    public async Task An_image_only_control_yields_a_png_of_exactly_that_control()
    {
        using var target = TestTargetLauncher.Launch();
        Press(target, "ShowSampleImageButton");
        await Task.Delay(300);

        var result = await CaptureFileAsync(target, "PrescriptionImage");

        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        var attachment = Assert.IsType<CapturedAttachment>(result.Attachment);
        Assert.Equal(AttachmentSource.Screen, attachment.Source);
        Assert.Equal("image/png", attachment.ContentType);

        var frame = BitmapDecoder.Create(new MemoryStream(attachment.Content), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bounds = Find(target, "PrescriptionImage").Current.BoundingRectangle;
        Assert.InRange(frame.PixelWidth, (int)bounds.Width - 2, (int)bounds.Width + 2);
        Assert.InRange(frame.PixelHeight, (int)bounds.Height - 2, (int)bounds.Height + 2);

        // The sample is red on the left half and blue on the right half.
        var left = Pixel(frame, frame.PixelWidth / 4, frame.PixelHeight / 2);
        var right = Pixel(frame, frame.PixelWidth * 3 / 4, frame.PixelHeight / 2);
        Assert.True(left.R > 200 && left.B < 60, $"Expected red on the left, got {left}.");
        Assert.True(right.B > 200 && right.R < 60, $"Expected blue on the right, got {right}.");
    }

    [Fact]
    public async Task An_image_covered_by_another_window_is_refused_instead_of_capturing_that_window()
    {
        using var target = TestTargetLauncher.Launch();
        Press(target, "ShowSampleImageButton");
        var bounds = Find(target, "PrescriptionImage").Current.BoundingRectangle;

        // A second window placed over the image area.
        using var cover = TestTargetLauncher.Launch();
        var transform = (TransformPattern)cover.Window.GetCurrentPattern(TransformPattern.Pattern);
        transform.Move(bounds.X - 50, bounds.Y - 50);
        await Task.Delay(500);

        var result = await CaptureFileAsync(target, "PrescriptionImage");

        Assert.Equal(CaptureOutcome.Obscured, result.Outcome);
        Assert.Null(result.Attachment);
    }

    private static async Task<CaptureResult> CaptureFileAsync(TestTargetLauncher target, string automationId)
    {
        using var dispatcher = new AutomationDispatcher();
        using var resolver = new UiAutomationSelectorResolver(dispatcher, processId: target.Window.Current.ProcessId);
        var fingerprint = new ElementFingerprint("Prescriva.Agent.TestTarget", target.Window.Current.Name, AutomationId: automationId);
        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);
        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);

        using var capture = new UiAutomationCaptureProvider(dispatcher);
        return await capture.CaptureAsync(
            resolution.Handle!,
            new FieldDefinition("prescription", "budget", "Receita", Required: false, Selector: fingerprint, Kind: FieldKind.File),
            CancellationToken.None);
    }

    private static (byte R, byte G, byte B) Pixel(BitmapSource frame, int x, int y)
    {
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var buffer = new byte[4];
        converted.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return (buffer[2], buffer[1], buffer[0]);
    }

    private static AutomationElement Find(TestTargetLauncher target, string automationId) =>
        target.Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
        ?? throw new InvalidOperationException($"'{automationId}' not found.");

    private static void SetText(TestTargetLauncher target, string automationId, string value) =>
        ((ValuePattern)Find(target, automationId).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    private static void Press(TestTargetLauncher target, string automationId) =>
        ((InvokePattern)Find(target, automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
}
