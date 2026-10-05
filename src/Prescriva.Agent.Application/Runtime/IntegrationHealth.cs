using System.Collections.Immutable;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Sessions;

namespace Prescriva.Agent.Application.Runtime;

/// <summary>Overall state of an active integration (spec §8).</summary>
public enum IntegrationHealthState
{
    Healthy,

    /// <summary>Working, but a field was only found with low confidence - review its selector.</summary>
    Degraded,

    /// <summary>Something configured cannot be observed or read: events depending on it are not produced.</summary>
    Broken,
}

public enum IntegrationIssueKind
{
    /// <summary>A trigger's element cannot be watched (Broken).</summary>
    TriggerUnwatchable,

    /// <summary>A field could not be resolved or read when its trigger fired (Broken).</summary>
    FieldUnreadable,

    /// <summary>A field resolved, but below the confidence threshold (Degraded).</summary>
    LowConfidenceMatch,
}

/// <summary>One reason behind a non-healthy state. Carries IDs only, never a captured value.</summary>
public sealed record IntegrationIssue(IntegrationIssueKind Kind, string? TriggerId, string? FieldId, double? Confidence = null);

/// <summary>The current state and every reason for it, most severe first.</summary>
public sealed record IntegrationHealth(IntegrationHealthState State, ImmutableArray<IntegrationIssue> Issues)
{
    public static readonly IntegrationHealth Healthy = new(IntegrationHealthState.Healthy, ImmutableArray<IntegrationIssue>.Empty);
}

/// <summary>
/// Derives <see cref="IntegrationHealth"/> from the runtime's own <see cref="RuntimeDiagnostic"/>s:
/// a trigger that cannot be watched or a field that cannot be read makes the integration
/// Broken; a low-confidence match makes it Degraded. Each issue clears when the evidence
/// says the problem stopped: the trigger is watched again, or its trigger persists an event
/// with that field captured cleanly. An operator leaving a required field empty is not a
/// configuration problem and does not affect health. Not thread-safe: callers serialize.
/// </summary>
public sealed class IntegrationHealthTracker
{
    private readonly ImmutableDictionary<string, ImmutableArray<string>> _fieldsByTrigger;
    private readonly HashSet<string> _unwatchableTriggers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadableFields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double?> _lowConfidenceFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lowConfidenceInCurrentOccurrence = new(StringComparer.Ordinal);

    public IntegrationHealthTracker(IntegrationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _fieldsByTrigger = configuration.Triggers.ToImmutableDictionary(
            trigger => trigger.Id,
            trigger => trigger.Actions.OfType<CaptureFieldsAction>().SelectMany(action => action.FieldIds).Distinct().ToImmutableArray(),
            StringComparer.Ordinal);
    }

    public IntegrationHealth Current { get; private set; } = IntegrationHealth.Healthy;

    public IntegrationHealth Apply(RuntimeDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        switch (diagnostic.Code)
        {
            case RuntimeDiagnosticCode.TriggerWatchFailed when diagnostic.TriggerId is { } failedTrigger:
                _unwatchableTriggers.Add(failedTrigger);
                break;

            case RuntimeDiagnosticCode.TriggerWatchStarted when diagnostic.TriggerId is { } watchedTrigger:
                _unwatchableTriggers.Remove(watchedTrigger);
                break;

            case RuntimeDiagnosticCode.SelectorFallback when diagnostic.FieldId is { } weakField:
                _lowConfidenceFields[weakField] = diagnostic.Confidence;
                _lowConfidenceInCurrentOccurrence.Add(weakField);
                break;

            case RuntimeDiagnosticCode.SessionRejected when diagnostic.FailureCode == SessionFailureCode.CaptureFailed && diagnostic.FieldId is { } unreadableField:
                _unreadableFields.Add(unreadableField);
                ClearOccurrence(diagnostic.TriggerId);
                break;

            case RuntimeDiagnosticCode.SessionRejected:
            case RuntimeDiagnosticCode.SessionIgnored:
                ClearOccurrence(diagnostic.TriggerId);
                break;

            case RuntimeDiagnosticCode.EventPersisted when diagnostic.TriggerId is { } persistedTrigger:
                foreach (var field in FieldsOf(persistedTrigger))
                {
                    _unreadableFields.Remove(field);
                    if (!_lowConfidenceInCurrentOccurrence.Contains(field))
                    {
                        _lowConfidenceFields.Remove(field);
                    }
                }

                ClearOccurrence(persistedTrigger);
                break;
        }

        Current = Evaluate();
        return Current;
    }

    private void ClearOccurrence(string? triggerId)
    {
        foreach (var field in FieldsOf(triggerId))
        {
            _lowConfidenceInCurrentOccurrence.Remove(field);
        }
    }

    private ImmutableArray<string> FieldsOf(string? triggerId) =>
        triggerId is not null && _fieldsByTrigger.TryGetValue(triggerId, out var fields) ? fields : ImmutableArray<string>.Empty;

    private IntegrationHealth Evaluate()
    {
        var issues = ImmutableArray.CreateBuilder<IntegrationIssue>();
        issues.AddRange(_unreadableFields.Order(StringComparer.Ordinal).Select(field => new IntegrationIssue(IntegrationIssueKind.FieldUnreadable, null, field)));
        issues.AddRange(_unwatchableTriggers.Order(StringComparer.Ordinal).Select(trigger => new IntegrationIssue(IntegrationIssueKind.TriggerUnwatchable, trigger, null)));
        issues.AddRange(_lowConfidenceFields.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new IntegrationIssue(IntegrationIssueKind.LowConfidenceMatch, null, pair.Key, pair.Value)));

        var state = _unreadableFields.Count > 0 || _unwatchableTriggers.Count > 0
            ? IntegrationHealthState.Broken
            : _lowConfidenceFields.Count > 0 ? IntegrationHealthState.Degraded : IntegrationHealthState.Healthy;
        return new IntegrationHealth(state, issues.ToImmutable());
    }
}
