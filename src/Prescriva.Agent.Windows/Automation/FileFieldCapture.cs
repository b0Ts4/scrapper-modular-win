using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Captures a <see cref="FieldKind.File"/> field. If the control exposes text, it must be the
/// full path of an existing file, which is copied exactly (bounded at
/// <see cref="AttachmentLimits.MaxBytes"/>). If it exposes no text - an image or drop zone -
/// the control's own on-screen rectangle is captured as PNG, and only when nothing (including
/// another application's window) covers it, so no other content is ever recorded.
/// </summary>
internal static class FileFieldCapture
{
    private const int SamplingInset = 3;
    private const int MaxParentHops = 12;

    /// <summary>What the dispatcher-thread step found: a path to read, a screen image, or a failure.</summary>
    internal sealed record Probe(string? Path, byte[]? Png, CaptureOutcome? Failure, string Detail);

    /// <summary>Runs on the AutomationDispatcher STA thread.</summary>
    public static Probe ProbeOnDispatcherThread(AutomationElement element)
    {
        var current = element.Current; // live round-trip: throws if the element is gone
        var text = ReadText(element);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return new Probe(text.Trim(), null, null, "path");
        }

        if (current.IsOffscreen)
        {
            return new Probe(null, null, CaptureOutcome.Obscured, "The control is off screen.");
        }

        var bounds = current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
        {
            return new Probe(null, null, CaptureOutcome.FileUnavailable, "The control shows no content.");
        }

        if (!IsFullyVisible(element, bounds))
        {
            return new Probe(null, null, CaptureOutcome.Obscured, "Another window covers the control.");
        }

        return new Probe(null, CapturePng(bounds), null, "screen");
    }

    /// <summary>Reads the file a path probe found; runs off the dispatcher thread.</summary>
    public static async Task<(CaptureOutcome Outcome, CapturedAttachment? Attachment)> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            return (CaptureOutcome.FileUnavailable, null);
        }

        try
        {
            if (new FileInfo(path).Length > AttachmentLimits.MaxBytes)
            {
                return (CaptureOutcome.TooLarge, null);
            }

            var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (content.LongLength > AttachmentLimits.MaxBytes)
            {
                return (CaptureOutcome.TooLarge, null); // grew between the check and the read
            }

            var fileName = System.IO.Path.GetFileName(path);
            return (CaptureOutcome.Captured, new CapturedAttachment(content, fileName, ContentTypeOf(fileName), AttachmentSource.File));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (CaptureOutcome.FileUnavailable, null);
        }
    }

    public static string ContentTypeOf(string fileName) => System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream",
    };

    private static string? ReadText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) && valuePattern is ValuePattern value)
        {
            return value.Current.Value;
        }

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern) && textPattern is TextPattern textual)
        {
            return textual.DocumentRange.GetText(-1);
        }

        return null;
    }

    /// <summary>
    /// The control is topmost at its centre and near each corner: the element found there is
    /// the control itself or one of its descendants (never another window, including this
    /// process's own windows such as the highlight overlay).
    /// </summary>
    private static bool IsFullyVisible(AutomationElement element, System.Windows.Rect bounds)
    {
        var points = new[]
        {
            new System.Windows.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2),
            new System.Windows.Point(bounds.Left + SamplingInset, bounds.Top + SamplingInset),
            new System.Windows.Point(bounds.Right - SamplingInset, bounds.Top + SamplingInset),
            new System.Windows.Point(bounds.Left + SamplingInset, bounds.Bottom - SamplingInset),
            new System.Windows.Point(bounds.Right - SamplingInset, bounds.Bottom - SamplingInset),
        };

        foreach (var point in points)
        {
            AutomationElement? hit;
            try
            {
                hit = AutomationElement.FromPoint(point);
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }

            if (!IsSelfOrDescendant(hit, element))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSelfOrDescendant(AutomationElement? candidate, AutomationElement element)
    {
        var walker = TreeWalker.RawViewWalker;
        for (var hops = 0; candidate is not null && hops < MaxParentHops; hops++)
        {
            if (System.Windows.Automation.Automation.Compare(candidate, element))
            {
                return true;
            }

            candidate = walker.GetParent(candidate);
        }

        return false;
    }

    private static byte[] CapturePng(System.Windows.Rect bounds)
    {
        const int SrcCopy = 0x00CC0020;
        const int CaptureBlt = 0x40000000;

        var x = (int)Math.Round(bounds.X);
        var y = (int)Math.Round(bounds.Y);
        var width = (int)Math.Round(bounds.Width);
        var height = (int)Math.Round(bounds.Height);

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SrcCopy | CaptureBlt);
            SelectObject(memoryDc, previous);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}
