namespace Prescriva.Agent.Application.Inspection;

public enum InspectionOutcome
{
    Found,
    TimedOut,
    WindowMissing,
    ElementUnavailable,
}

/// <summary>
/// The outcome of a single point-inspection request. Only <see cref="InspectionOutcome.Found"/>
/// carries a <see cref="Snapshot"/>; every other outcome is a stable, typed failure code that
/// callers can branch on without catching native UI Automation exceptions.
/// </summary>
public sealed record InspectionResult(InspectionOutcome Outcome, ElementSnapshot? Snapshot = null, string? FailureReason = null)
{
    public static InspectionResult Found(ElementSnapshot snapshot) =>
        new(InspectionOutcome.Found, snapshot);

    public static InspectionResult TimedOut() =>
        new(InspectionOutcome.TimedOut);

    public static InspectionResult WindowMissing(string? reason = null) =>
        new(InspectionOutcome.WindowMissing, FailureReason: reason);

    public static InspectionResult ElementUnavailable(string? reason = null) =>
        new(InspectionOutcome.ElementUnavailable, FailureReason: reason);
}
