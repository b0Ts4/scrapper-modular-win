using System.Collections.Immutable;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Inspection;

public enum InspectionSessionStatus
{
    /// <summary>No inspection session is running; nothing should be highlighted.</summary>
    NotStarted,

    /// <summary>A session is running. There may or may not currently be a highlighted element.</summary>
    Active,

    /// <summary>The session was stopped; nothing should be highlighted.</summary>
    Stopped,

    /// <summary>The user confirmed a selection; <see cref="InspectionState.Snapshot"/> and
    /// <see cref="InspectionState.Fingerprint"/> hold what was displayed at that moment.</summary>
    Confirmed,
}

/// <summary>
/// The complete, immutable state of a live inspection session, as produced by
/// <see cref="InspectionController"/>. Deliberately plain data - a WPF view model can
/// bind to it, or a test can assert on it, without any dependency on UI Automation.
/// </summary>
public sealed record InspectionState(
    InspectionSessionStatus Status,
    ElementSnapshot? Snapshot,
    BoundingRectangle? Bounds,
    ElementFingerprint? Fingerprint,
    ImmutableArray<string> Warnings)
{
    public static readonly InspectionState NotStarted =
        new(InspectionSessionStatus.NotStarted, null, null, null, ImmutableArray<string>.Empty);

    public static InspectionState Active(ElementSnapshot? snapshot, ElementFingerprint? fingerprint, ImmutableArray<string> warnings) =>
        new(InspectionSessionStatus.Active, snapshot, snapshot?.BoundingRectangle, fingerprint, warnings);

    public static InspectionState Stopped() =>
        new(InspectionSessionStatus.Stopped, null, null, null, ImmutableArray<string>.Empty);

    public static InspectionState Confirmed(ElementSnapshot snapshot, ElementFingerprint fingerprint) =>
        new(InspectionSessionStatus.Confirmed, snapshot, snapshot.BoundingRectangle, fingerprint, ImmutableArray<string>.Empty);
}
