using System.Collections.Immutable;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Inspection;

/// <summary>
/// A plain, immutable copy of the data describing a UI element at the moment it was
/// inspected. Deliberately holds no reference to any native UI Automation type, so it
/// can be captured, serialized, or compared long after the live element is gone.
/// </summary>
/// <remarks>
/// <see cref="ProcessId"/>, <see cref="ProcessName"/> and <see cref="WindowTitle"/> exist
/// primarily so Application-layer code (notably <c>InspectionController</c>) can (a)
/// recognize and exclude elements that belong to the Agent's own process - most
/// importantly its own click-through highlight overlay, which UI Automation hit-testing
/// can still return even though the overlay is invisible to mouse input - and (b) build a
/// <see cref="Prescriva.Agent.Domain.Selectors.ElementFingerprint"/> (which needs a
/// process identity and window rule) from a point-inspection result without re-querying
/// native UI Automation state that may have already changed.
/// </remarks>
public sealed record ElementSnapshot(
    string? AutomationId,
    string? Name,
    string ControlType,
    string? ClassName,
    BoundingRectangle BoundingRectangle,
    int ProcessId,
    string ProcessName,
    string? WindowTitle,
    ImmutableArray<AncestorFingerprint> Ancestors = default,
    ImmutableArray<string> NearbyLabels = default,
    RelativeBounds? RelativeBounds = null,
    string? FrameworkId = null);

/// <summary>
/// A plain bounding rectangle in screen coordinates.
/// </summary>
public readonly record struct BoundingRectangle(double X, double Y, double Width, double Height);
