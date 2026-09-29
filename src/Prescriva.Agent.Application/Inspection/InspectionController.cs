using System.Collections.Immutable;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Inspection;

/// <summary>
/// Orchestrates a live, pointer-driven inspection session on top of
/// <see cref="IElementInspector"/>. Pure Application-layer logic: it never touches UI
/// Automation directly, only ever sees the plain data <see cref="IElementInspector"/>
/// returns, and is fully testable with a fake inspector.
///
/// Three responsibilities beyond a thin pass-through of <see cref="IElementInspector"/>:
///  - Throttling: rapid pointer movement must not queue up a backlog of stale
///    `FromPointAsync` calls. Each call to <see cref="ObservePointerAsync"/> cancels
///    whichever inspection (if any) is still in flight from a previous call.
///  - Exclusion: an element belonging to the Agent's own process (most importantly its
///    own click-through highlight overlay, which UI Automation hit-testing can still
///    return even though the overlay is invisible to mouse input) is never surfaced as a
///    found element.
///  - Confirmation fidelity: <see cref="ConfirmAsync"/> captures whatever was already
///    displayed in <see cref="CurrentState"/> - it never re-resolves against the live
///    inspector, so a confirmed selection can never differ from what the user saw.
/// </summary>
public sealed class InspectionController
{
    private readonly IElementInspector _inspector;
    private readonly TimeSpan _timeout;
    private readonly HashSet<int> _excludedProcessIds;
    private readonly object _gate = new();

    private InspectionState _state = InspectionState.NotStarted;
    private CancellationTokenSource? _pointerCts;

    public InspectionController(
        IElementInspector inspector,
        TimeSpan? timeout = null,
        IEnumerable<int>? additionalExcludedProcessIds = null)
    {
        ArgumentNullException.ThrowIfNull(inspector);

        _inspector = inspector;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);

        // The current process is always excluded - this is what keeps the Agent's own
        // highlight overlay (hosted in this same process) from ever being reported as a
        // found element. Callers may add further pids (e.g. a separate helper process).
        _excludedProcessIds = new HashSet<int> { Environment.ProcessId };
        if (additionalExcludedProcessIds is not null)
        {
            foreach (var processId in additionalExcludedProcessIds)
            {
                _excludedProcessIds.Add(processId);
            }
        }
    }

    /// <summary>Raised every time <see cref="CurrentState"/> changes.</summary>
    public event EventHandler<InspectionState>? StateChanged;

    public InspectionState CurrentState
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Begins a session: no element is highlighted yet until a pointer position is observed.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        SetState(InspectionState.Active(null, null, ImmutableArray<string>.Empty));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Requests inspection of whatever is under <paramref name="point"/>. Cancels any
    /// inspection still in flight from a previous call to this method, so rapid pointer
    /// movement never queues up a backlog of stale requests - only the most recent
    /// observation is ever allowed to update <see cref="CurrentState"/>.
    /// </summary>
    public async Task ObservePointerAsync(ScreenPoint point, CancellationToken cancellationToken = default)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pointerCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        // Captured once, up front: cts may be cancelled *and disposed* by a subsequent
        // call to this method while we are awaiting below (a superseding call disposes
        // the CancellationTokenSource it replaces). Reading the `Token` property on a
        // disposed CancellationTokenSource throws ObjectDisposedException, which - if it
        // happened inside the catch filter below - would prevent the filter from ever
        // matching. A CancellationToken value obtained before disposal remains safe to
        // read (IsCancellationRequested, etc.) even after its source is disposed.
        var ownToken = cts.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ownToken, cancellationToken);

        InspectionResult result;
        try
        {
            result = await _inspector.FromPointAsync(point, _timeout, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ownToken.IsCancellationRequested)
        {
            // Superseded by a newer ObservePointerAsync call - its result (or a later
            // one) already reflects the current pointer position, so there is nothing
            // left for this stale call to apply.
            return;
        }

        // Even if this call's own inspection completed normally, a newer call may have
        // started (and possibly already finished) while we were awaiting. Never let an
        // older result clobber a newer one.
        if (!ReferenceEquals(Volatile.Read(ref _pointerCts), cts))
        {
            return;
        }

        ApplyResult(result);
    }

    /// <summary>Stops the session: removes any highlight and clears the current element.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        var previous = Interlocked.Exchange(ref _pointerCts, null);
        previous?.Cancel();
        previous?.Dispose();

        SetState(InspectionState.Stopped());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Confirms the element currently displayed in <see cref="CurrentState"/>. Captures
    /// exactly that snapshot and fingerprint - it deliberately does not call back into
    /// <see cref="IElementInspector"/>, so the confirmed selection can never differ from
    /// what was actually shown to the user at the moment they confirmed it.
    /// </summary>
    public Task<InspectionState> ConfirmAsync(CancellationToken cancellationToken = default)
    {
        InspectionState current;
        lock (_gate)
        {
            current = _state;
        }

        if (current.Snapshot is null || current.Fingerprint is null)
        {
            throw new InvalidOperationException(
                "Cannot confirm a selection: no element is currently displayed.");
        }

        var confirmed = InspectionState.Confirmed(current.Snapshot, current.Fingerprint);
        SetState(confirmed);
        return Task.FromResult(confirmed);
    }

    private void ApplyResult(InspectionResult result)
    {
        if (result.Outcome == InspectionOutcome.Found && result.Snapshot is { } snapshot)
        {
            if (_excludedProcessIds.Contains(snapshot.ProcessId))
            {
                SetState(InspectionState.Active(
                    null,
                    null,
                    ImmutableArray.Create(
                        "Ignored an element belonging to the Agent's own process (expected when the pointer is over the highlight overlay).")));
                return;
            }

            var fingerprint = BuildFingerprint(snapshot);
            SetState(InspectionState.Active(snapshot, fingerprint, ImmutableArray<string>.Empty));
            return;
        }

        var warning = result.Outcome switch
        {
            InspectionOutcome.TimedOut => "The inspection timed out before an element could be resolved.",
            InspectionOutcome.WindowMissing => result.FailureReason ?? "The target window is no longer available.",
            InspectionOutcome.ElementUnavailable => result.FailureReason ?? "The element under the pointer is no longer available.",
            _ => "No element was found at the given point.",
        };

        SetState(InspectionState.Active(null, null, ImmutableArray.Create(warning)));
    }

    private static ElementFingerprint BuildFingerprint(ElementSnapshot snapshot) =>
        new(
            ProcessIdentity: snapshot.ProcessName,
            WindowRule: snapshot.WindowTitle ?? string.Empty,
            AutomationId: snapshot.AutomationId,
            Name: snapshot.Name,
            ControlType: snapshot.ControlType,
            ClassName: snapshot.ClassName);

    private void SetState(InspectionState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        StateChanged?.Invoke(this, state);
    }
}
