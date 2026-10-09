using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Domain.Configuration;

/// <summary>What a configured field captures.</summary>
public enum FieldKind
{
    /// <summary>The control's text or selection (the default).</summary>
    Text = 0,

    /// <summary>
    /// A file or image: the original file when the control exposes its path, otherwise
    /// the image the control shows. The captured value is an attachment reference; the
    /// content is stored separately and protected at rest.
    /// </summary>
    File = 1,

    /// <summary>
    /// Text read by optical character recognition from the image the control shows - for
    /// text that UI Automation does not expose (drawn text, a scanned document shown as an
    /// image). The captured value is plain text, like <see cref="Text"/>.
    /// </summary>
    OcrText = 2,

    /// <summary>
    /// An image the program only shows on screen (not a file, not an input): the control's own
    /// on-screen rectangle is always captured as PNG - whatever text the control exposes - and
    /// only when nothing covers it. The captured value is an attachment reference, like
    /// <see cref="File"/>.
    /// </summary>
    ScreenImage = 3,
}

public static class FieldKindExtensions
{
    /// <summary>True when the field's value is an attachment (a file or an image), not text.</summary>
    public static bool ProducesAttachment(this FieldKind kind) => kind is FieldKind.File or FieldKind.ScreenImage;
}

public sealed record FieldDefinition(
    string Id,
    string StageId,
    string Meaning,
    bool Required,
    ElementFingerprint Selector,
    FieldKind Kind = FieldKind.Text);
