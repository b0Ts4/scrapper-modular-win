namespace Prescriva.Agent.Application.Inspection;

/// <summary>
/// A plain screen coordinate (physical pixels) used to request an element inspection.
/// Carries no reference to any native UI Automation type.
/// </summary>
public readonly record struct ScreenPoint(double X, double Y);
