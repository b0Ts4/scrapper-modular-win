namespace Prescriva.Agent.Domain.Selectors;

public sealed record AncestorFingerprint(
    string? AutomationId = null,
    string? ControlType = null,
    string? Name = null);
