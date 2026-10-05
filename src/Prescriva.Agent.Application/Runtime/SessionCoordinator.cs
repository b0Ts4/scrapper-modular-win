using System.Collections.Immutable;
using System.Diagnostics;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Diagnostics;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Sessions;

namespace Prescriva.Agent.Application.Runtime;

/// <summary>
/// Owns exactly one <see cref="CaptureSession"/> for exactly one running application
/// instance and drives it to completion: watches every configured trigger (via
/// <see cref="ITriggerProvider"/>), resolves and captures whatever fields a firing
/// trigger's actions require (via <see cref="ISelectorResolver"/> and
/// <see cref="ICaptureProvider"/>), feeds the result into <see cref="SessionEngine"/>, and
/// persists any emitted events (via <see cref="IEventOutbox"/>) before publishing a
/// success diagnostic - so a diagnostic never claims success for an event that did not
/// actually get persisted.
///
/// All state mutation is serialized through <see cref="_gate"/>: at most one trigger
/// occurrence (or the application-close notification) is ever being applied to this
/// session at a time. Different <see cref="SessionCoordinator"/> instances - one per
/// application instance - share no state and never block on each other; that is what lets
/// two running instances of the same configured application progress completely
/// independently (see <see cref="AgentRuntime"/>).
/// </summary>
public sealed class SessionCoordinator
{
    private readonly IntegrationConfiguration _configuration;
    private readonly SessionEngine _engine;
    private readonly ITriggerProvider _triggerProvider;
    private readonly ISelectorResolver _resolver;
    private readonly ICaptureProvider _captureProvider;
    private readonly IEventOutbox _outbox;
    private readonly ITechnicalLog _log;
    private readonly Func<Guid> _eventIdFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _triggerRetryDelay;
    private readonly IAttachmentStore? _attachments;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _failingTriggers = new(StringComparer.Ordinal);

    private CaptureSession _session;

    public SessionCoordinator(
        Guid sessionId,
        string initialStageId,
        IntegrationConfiguration configuration,
        ITriggerProvider triggerProvider,
        ISelectorResolver resolver,
        ICaptureProvider captureProvider,
        IEventOutbox outbox,
        ITechnicalLog log,
        Func<Guid>? eventIdFactory = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? triggerRetryDelay = null,
        IAttachmentStore? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialStageId);
        ArgumentNullException.ThrowIfNull(triggerProvider);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(captureProvider);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(log);

        _configuration = configuration;
        _engine = new SessionEngine(configuration, Testing.ConfigurationFingerprint.Compute(configuration));
        _session = CaptureSession.Start(sessionId, initialStageId);
        _triggerProvider = triggerProvider;
        _resolver = resolver;
        _captureProvider = captureProvider;
        _outbox = outbox;
        _log = log;
        _eventIdFactory = eventIdFactory ?? Guid.NewGuid;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _triggerRetryDelay = triggerRetryDelay ?? DefaultTriggerRetryDelay;
        _attachments = attachments;
        _triggerProvider.WatchEstablished += OnWatchEstablished;
    }

    public Guid SessionId => _session.Id;

    /// <summary>A snapshot of the session's current state. Safe to read from any thread.</summary>
    public CaptureSession CurrentSession => Volatile.Read(ref _session);

    /// <summary>
    /// Raised once per handled trigger occurrence or application-close notification.
    /// Never carries a captured field value - see <see cref="RuntimeDiagnostic"/>.
    /// </summary>
    public event EventHandler<RuntimeDiagnostic>? DiagnosticPublished;

    /// <summary>
    /// Watches every trigger configured for this session's configuration concurrently,
    /// applying each occurrence in turn, until <paramref name="cancellationToken"/> is
    /// cancelled or every watch completes on its own (e.g. a fake provider in tests
    /// yielding a fixed number of signals).
    /// </summary>
    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_configuration.Triggers.Select(trigger => WatchTriggerAsync(trigger, cancellationToken)));

    /// <summary>
    /// Ends this session because the application instance it belongs to closed. Not driven
    /// through a configured trigger (there is no UI element left to fire one), so this
    /// mirrors <see cref="SessionEngine"/>'s own Cancel semantics directly: accumulated
    /// values and confirmed items are cleared, the stage and used event IDs are preserved
    /// for inspection, no event is emitted, and a single informational diagnostic is
    /// published. A session that already ended (Finished or Cancelled) is left untouched.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        var elapsed = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session.State != SessionState.Active)
            {
                return;
            }

            _session = _session with
            {
                State = SessionState.Cancelled,
                Values = ImmutableDictionary<string, string>.Empty,
                ConfirmedItems = ImmutableArray<ImmutableDictionary<string, string>>.Empty
            };

            var now = _clock();
            _log.Log(new TechnicalLogEntry(
                TechnicalLogLevel.Info,
                nameof(RuntimeDiagnosticCode.SessionClosed),
                "The application instance closed; the session was ended.",
                now,
                SessionId: _session.Id,
                Elapsed: elapsed.Elapsed));

            Publish(new RuntimeDiagnostic(
                RuntimeDiagnosticCode.SessionClosed,
                RuntimeDiagnosticSeverity.Info,
                _session.Id,
                now,
                elapsed.Elapsed));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How long to wait before re-establishing a trigger watch that failed.</summary>
    private static readonly TimeSpan DefaultTriggerRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Watches one trigger until cancelled. A watch that fails for any other reason (its
    /// element cannot be resolved yet, or disappears - e.g. the button belongs to a screen
    /// the application is not showing right now) never faults <see cref="RunAsync"/>: the
    /// first failure of a streak that outlives <see cref="_triggerRetryDelay"/> is reported
    /// as a typed <see cref="RuntimeDiagnosticCode.TriggerWatchFailed"/> error (a session
    /// ended within that delay - the application closing - reports nothing), the watch is
    /// retried every <see cref="_triggerRetryDelay"/> until cancelled, and a recovered watch is
    /// announced again through <see cref="RuntimeDiagnosticCode.TriggerWatchStarted"/>.
    /// The session's other triggers keep being observed throughout.
    /// </summary>
    private async Task WatchTriggerAsync(TriggerDefinition trigger, CancellationToken cancellationToken)
    {
        while (true)
        {
            var deduplicator = new TriggerDeduplicator();
            try
            {
                await foreach (var signal in _triggerProvider.WatchAsync(trigger, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (!deduplicator.Accept(signal))
                    {
                        continue;
                    }

                    await HandleTriggerAsync(trigger, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Report only once the failure outlives the retry delay: when the
                // application closes, its elements vanish a moment before the session is
                // ended, and that must not surface as an error.
                await Task.Delay(_triggerRetryDelay, cancellationToken).ConfigureAwait(false);
                if (_failingTriggers.TryAdd(trigger.Id, true))
                {
                    PublishTriggerWatchFailed(trigger, exception);
                }

                continue;
            }
        }
    }

    private void PublishTriggerWatchFailed(TriggerDefinition trigger, Exception exception)
    {
        var now = _clock();

        // Only the exception type is logged: provider messages are technical, but the
        // log contract is IDs, codes and metadata only.
        _log.Log(new TechnicalLogEntry(
            TechnicalLogLevel.Error,
            nameof(RuntimeDiagnosticCode.TriggerWatchFailed),
            $"Trigger '{trigger.Id}' cannot be watched ({exception.GetType().Name}); retrying.",
            now,
            SessionId: SessionId,
            TriggerId: trigger.Id));

        Publish(new RuntimeDiagnostic(
            RuntimeDiagnosticCode.TriggerWatchFailed,
            RuntimeDiagnosticSeverity.Error,
            SessionId,
            now,
            TimeSpan.Zero,
            TriggerId: trigger.Id));
    }

    private void OnWatchEstablished(object? sender, string triggerId)
    {
        _failingTriggers.TryRemove(triggerId, out _);
        var now = _clock();
        _log.Log(new TechnicalLogEntry(
            TechnicalLogLevel.Info,
            nameof(RuntimeDiagnosticCode.TriggerWatchStarted),
            $"Trigger '{triggerId}' is being monitored.",
            now,
            SessionId: SessionId,
            TriggerId: triggerId));

        Publish(new RuntimeDiagnostic(
            RuntimeDiagnosticCode.TriggerWatchStarted,
            RuntimeDiagnosticSeverity.Info,
            SessionId,
            now,
            TimeSpan.Zero,
            TriggerId: triggerId));
    }

    /// <summary>
    /// Resolves and captures whatever fields <paramref name="trigger"/>'s actions require,
    /// then applies the resulting occurrence to the session. Serialized by <see cref="_gate"/>
    /// so occurrences for this one session are always applied one at a time, in order.
    /// </summary>
    private async Task HandleTriggerAsync(TriggerDefinition trigger, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session.State != SessionState.Active)
            {
                return;
            }

            var values = new Dictionary<string, CapturedFieldValue>();

            // A trigger whose stage does not match the session's current stage is always
            // Ignored by SessionEngine.Apply, regardless of supplied values (it checks stage
            // before ever looking at them) - skip resolving/capturing anything in that case,
            // so an irrelevant trigger firing never touches the live UI or raises a fallback
            // diagnostic for fields the session isn't even asking about right now.
            if (trigger.StageId == _session.CurrentStageId)
            {
                var fieldIds = trigger.Actions
                    .OfType<CaptureFieldsAction>()
                    .SelectMany(action => action.FieldIds)
                    .Distinct()
                    .ToArray();

                foreach (var fieldId in fieldIds)
                {
                    values[fieldId] = await CaptureFieldAsync(trigger, fieldId, cancellationToken).ConfigureAwait(false);
                }
            }

            var eventIdCount = trigger.Actions.Count(action => action is EmitEventAction or FinishSessionAction);
            var eventIds = ImmutableArray.CreateRange(Enumerable.Range(0, eventIdCount).Select(_ => _eventIdFactory()));
            var occurrence = new TriggerOccurrence(trigger.Id, eventIds);
            var now = _clock();

            var result = _engine.Apply(_session, occurrence, values, now);
            switch (result.Status)
            {
                case SessionTransitionStatus.Applied:
                    foreach (var domainEvent in result.Events)
                    {
                        // Append before publishing any success diagnostic: a diagnostic
                        // must never claim an event was persisted before it actually was.
                        await _outbox.AppendAsync(domainEvent, cancellationToken).ConfigureAwait(false);
                    }

                    _session = result.Session;

                    foreach (var domainEvent in result.Events)
                    {
                        _log.Log(new TechnicalLogEntry(
                            TechnicalLogLevel.Info,
                            nameof(RuntimeDiagnosticCode.EventPersisted),
                            $"Event '{domainEvent.Type}' was applied and persisted.",
                            now,
                            SessionId: _session.Id,
                            TriggerId: trigger.Id,
                            Elapsed: elapsed.Elapsed));

                        Publish(new RuntimeDiagnostic(
                            RuntimeDiagnosticCode.EventPersisted,
                            RuntimeDiagnosticSeverity.Info,
                            _session.Id,
                            now,
                            elapsed.Elapsed,
                            TriggerId: trigger.Id,
                            EventType: domainEvent.Type));
                    }

                    break;

                case SessionTransitionStatus.Rejected:
                    var failure = result.Failures.IsDefaultOrEmpty ? null : result.Failures[0];
                    _log.Log(new TechnicalLogEntry(
                        TechnicalLogLevel.Error,
                        nameof(RuntimeDiagnosticCode.SessionRejected),
                        $"Trigger '{trigger.Id}' was rejected: {failure?.Code}.",
                        now,
                        SessionId: _session.Id,
                        TriggerId: trigger.Id,
                        FieldId: failure?.FieldId,
                        Elapsed: elapsed.Elapsed));

                    Publish(new RuntimeDiagnostic(
                        RuntimeDiagnosticCode.SessionRejected,
                        RuntimeDiagnosticSeverity.Error,
                        _session.Id,
                        now,
                        elapsed.Elapsed,
                        TriggerId: trigger.Id,
                        FieldId: failure?.FieldId,
                        FailureCode: failure?.Code));
                    break;

                case SessionTransitionStatus.Ignored:
                    _log.Log(new TechnicalLogEntry(
                        TechnicalLogLevel.Debug,
                        nameof(RuntimeDiagnosticCode.SessionIgnored),
                        $"Trigger '{trigger.Id}' was ignored (not applicable to the current stage).",
                        now,
                        SessionId: _session.Id,
                        TriggerId: trigger.Id,
                        Elapsed: elapsed.Elapsed));

                    Publish(new RuntimeDiagnostic(
                        RuntimeDiagnosticCode.SessionIgnored,
                        RuntimeDiagnosticSeverity.Info,
                        _session.Id,
                        now,
                        elapsed.Elapsed,
                        TriggerId: trigger.Id));
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Below this confidence, a <see cref="SelectorResolutionStatus.Found"/> match is still
    /// used - the resolver itself already decided it was safe to act on - but is flagged
    /// with a <see cref="RuntimeDiagnosticCode.SelectorFallback"/> diagnostic so an operator
    /// can review a match that, while accepted, was not a clean lead over the next best
    /// candidate.
    /// </summary>
    private const double FallbackConfidenceThreshold = 0.8;

    /// <summary>
    /// Resolves the selector for <paramref name="fieldId"/> and, if found, reads its value.
    /// A resolution that is not <see cref="SelectorResolutionStatus.Found"/> (ambiguous, not
    /// found, window missing, timed out) - or a capture attempt that did not produce a value -
    /// is reported as a typed <see cref="CaptureFailure"/>; per <see cref="SessionEngine"/>'s
    /// own contract, any such failure rejects the whole trigger occurrence, even for an
    /// optional field, so the transition is never silently degraded. A clean
    /// <see cref="SelectorResolutionStatus.Found"/> match below <see cref="FallbackConfidenceThreshold"/>
    /// is different: it is still used, but raises a <see cref="RuntimeDiagnosticCode.SelectorFallback"/>
    /// diagnostic alongside the eventual success diagnostic.
    /// </summary>
    private async Task<CapturedFieldValue> CaptureFieldAsync(TriggerDefinition trigger, string fieldId, CancellationToken cancellationToken)
    {
        var field = _configuration.Fields.First(candidate => candidate.Id == fieldId);
        var fieldElapsed = Stopwatch.StartNew();

        var resolution = await _resolver.ResolveAsync(field.Selector, cancellationToken).ConfigureAwait(false);
        if (resolution.Status != SelectorResolutionStatus.Found || resolution.Handle is null)
        {
            var failure = resolution.Status switch
            {
                SelectorResolutionStatus.Ambiguous => CaptureFailure.Ambiguous,
                SelectorResolutionStatus.TimedOut => CaptureFailure.Unreadable,
                _ => CaptureFailure.Unavailable
            };

            _log.Log(new TechnicalLogEntry(
                TechnicalLogLevel.Warning,
                "selector_resolution_failed",
                $"Selector resolution for field '{fieldId}' did not produce a usable match (status: {resolution.Status}).",
                _clock(),
                SessionId: _session.Id,
                TriggerId: trigger.Id,
                FieldId: fieldId,
                Confidence: resolution.Confidence,
                Elapsed: fieldElapsed.Elapsed));

            return new CapturedFieldValue(null, failure);
        }

        if (resolution.Confidence < FallbackConfidenceThreshold)
        {
            PublishSelectorFallback(trigger, fieldId, resolution.Confidence, fieldElapsed.Elapsed);
        }

        var captureResult = await _captureProvider.CaptureAsync(resolution.Handle, field, cancellationToken).ConfigureAwait(false);
        fieldElapsed.Stop();

        _log.Log(new TechnicalLogEntry(
            TechnicalLogLevel.Debug,
            "field_capture_attempted",
            $"Field '{fieldId}' capture outcome: {captureResult.Outcome}.",
            _clock(),
            SessionId: _session.Id,
            TriggerId: trigger.Id,
            FieldId: fieldId,
            Provider: captureResult.ProviderId,
            Confidence: captureResult.Confidence,
            Elapsed: captureResult.Duration));

        if (captureResult.Outcome != CaptureOutcome.Captured)
        {
            var failure = captureResult.Outcome switch
            {
                CaptureOutcome.UnsupportedPattern => CaptureFailure.UnsupportedProvider,
                CaptureOutcome.TimedOut or CaptureOutcome.TooLarge => CaptureFailure.Unreadable,
                _ => CaptureFailure.Unavailable
            };

            return new CapturedFieldValue(null, failure);
        }

        if (field.Kind == FieldKind.File)
        {
            return await StoreAttachmentAsync(trigger, field, captureResult, cancellationToken).ConfigureAwait(false);
        }

        return new CapturedFieldValue(captureResult.Value);
    }

    /// <summary>
    /// A file field's value is the reference of its stored attachment. The attachment is
    /// stored here - before the session engine runs and long before the event referencing it
    /// is appended - so a persisted event never points at missing content. Any problem
    /// becomes a typed capture failure, which rejects the occurrence visibly.
    /// </summary>
    private async Task<CapturedFieldValue> StoreAttachmentAsync(
        TriggerDefinition trigger,
        FieldDefinition field,
        CaptureResult captureResult,
        CancellationToken cancellationToken)
    {
        if (captureResult.Attachment is not { } attachment)
        {
            return new CapturedFieldValue(null, CaptureFailure.Unavailable);
        }

        if (_attachments is null)
        {
            return new CapturedFieldValue(null, CaptureFailure.UnsupportedProvider);
        }

        try
        {
            var info = await _attachments.SaveAsync(attachment, cancellationToken).ConfigureAwait(false);
            _log.Log(new TechnicalLogEntry(
                TechnicalLogLevel.Debug,
                "attachment_stored",
                $"Field '{field.Id}' attachment stored ({info.Size} bytes, source {info.Source}).",
                _clock(),
                SessionId: _session.Id,
                TriggerId: trigger.Id,
                FieldId: field.Id));
            return new CapturedFieldValue(info.Reference);
        }
        catch (AttachmentTooLargeException)
        {
            return new CapturedFieldValue(null, CaptureFailure.Unreadable);
        }
    }

    private void PublishSelectorFallback(TriggerDefinition trigger, string fieldId, double confidence, TimeSpan elapsed)
    {
        var now = _clock();
        _log.Log(new TechnicalLogEntry(
            TechnicalLogLevel.Warning,
            nameof(RuntimeDiagnosticCode.SelectorFallback),
            $"Selector resolution for field '{fieldId}' did not produce a confident match.",
            now,
            SessionId: _session.Id,
            TriggerId: trigger.Id,
            FieldId: fieldId,
            Confidence: confidence,
            Elapsed: elapsed));

        Publish(new RuntimeDiagnostic(
            RuntimeDiagnosticCode.SelectorFallback,
            RuntimeDiagnosticSeverity.Warning,
            _session.Id,
            now,
            elapsed,
            TriggerId: trigger.Id,
            FieldId: fieldId,
            Confidence: confidence));
    }

    private void Publish(RuntimeDiagnostic diagnostic) => DiagnosticPublished?.Invoke(this, diagnostic);
}
