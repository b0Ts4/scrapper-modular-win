using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Reads an <see cref="Domain.Configuration.FieldKind.OcrText"/> field: the offline Windows OCR
/// (<c>Windows.Media.Ocr</c>) recognizes the text in the control's own on-screen image (taken
/// only when nothing covers it). Nothing leaves the machine and the image is discarded after
/// recognition. Small images are enlarged first, which the recognizer reads more reliably.
/// </summary>
public static class OcrFieldCapture
{
    public const string ProviderId = "windows-ocr";

    private const uint ComfortableHeight = 200;

    /// <summary>The recognized text and the language used; <see cref="Language"/> is null when no recognizer is installed.</summary>
    public sealed record OcrReading(string? Text, string? Language);

    /// <summary>The recognizer languages installed on this machine (e.g. "en-US", "pt-BR").</summary>
    public static IReadOnlyList<string> AvailableLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag).ToArray();

    public static async Task<OcrReading> RecognizeAsync(byte[] png, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(png);
        var tag = OcrText.ChooseLanguage(AvailableLanguages(), global::Windows.System.UserProfile.GlobalizationPreferences.Languages.ToArray());
        var engine = tag is null ? null : OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language(tag));
        if (engine is null)
        {
            return new OcrReading(null, null);
        }

        using var memory = new MemoryStream(png, writable: false);
        using var stream = memory.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

        var scale = decoder.PixelHeight >= ComfortableHeight ? 1u : Math.Min(4u, (ComfortableHeight + decoder.PixelHeight - 1) / Math.Max(1u, decoder.PixelHeight));
        while (scale > 1 && Math.Max(decoder.PixelWidth, decoder.PixelHeight) * scale > OcrEngine.MaxImageDimension)
        {
            scale--;
        }

        var transform = new BitmapTransform
        {
            ScaledWidth = decoder.PixelWidth * scale,
            ScaledHeight = decoder.PixelHeight * scale,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        return new OcrReading(OcrText.Normalize(result.Lines.Select(line => line.Text)), tag);
    }
}
