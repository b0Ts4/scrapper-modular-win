namespace Prescriva.Agent.Application.Inspection;

/// <summary>
/// A plain, immutable copy of the data describing a UI element at the moment it was
/// inspected. Deliberately holds no reference to any native UI Automation type, so it
/// can be captured, serialized, or compared long after the live element is gone.
/// </summary>
public sealed record ElementSnapshot(
    string? AutomationId,
    string? Name,
    string ControlType,
    string? ClassName,
    BoundingRectangle BoundingRectangle);

/// <summary>
/// A plain bounding rectangle in screen coordinates.
/// </summary>
public readonly record struct BoundingRectangle(double X, double Y, double Width, double Height);
