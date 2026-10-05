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
}

public sealed record FieldDefinition(
    string Id,
    string StageId,
    string Meaning,
    bool Required,
    ElementFingerprint Selector,
    FieldKind Kind = FieldKind.Text);
