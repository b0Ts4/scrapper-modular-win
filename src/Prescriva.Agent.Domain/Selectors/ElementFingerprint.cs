using System.Collections.Immutable;

namespace Prescriva.Agent.Domain.Selectors;

public sealed record ElementFingerprint(
    string ProcessIdentity,
    string WindowRule,
    string? AutomationId = null,
    string? Name = null,
    string? ControlType = null,
    string? ClassName = null,
    string? FrameworkId = null,
    ImmutableArray<AncestorFingerprint> Ancestors = default,
    ImmutableArray<string> NearbyLabels = default,
    RelativeBounds? RelativeBounds = null);

public readonly record struct RelativeBounds(double X, double Y, double Width, double Height);
