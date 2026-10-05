using System.Collections.Immutable;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Testing;

/// <summary>
/// Proves - against a live application, through the same <see cref="ISelectorResolver"/>,
/// <see cref="ICaptureProvider"/> and <see cref="ITriggerProvider"/> abstractions
/// <see cref="Runtime.SessionCoordinator"/> itself uses at runtime - that a configuration's
/// fields actually resolve and its triggers actually fire, before it is ever allowed to be
/// approved for activation ("Somente configurações testadas podem ser ativadas").
///
/// For every configured field: resolves its selector and, if found, reads its value once
/// (to surface which provider served it and at what confidence - never to persist or log
/// the value itself; see <see cref="FieldCheckResult"/>). For every configured trigger:
/// waits, up to <see cref="_triggerTimeout"/>, for at least one signal to be observed.
///
/// Deliberately does not drive the full <see cref="Domain.Sessions.SessionEngine"/> state
/// machine - that is what <see cref="Runtime.SessionCoordinator"/> already does, and is
/// covered by that runtime's own tests. This runner only needs to prove the underlying
/// resolution/capture/trigger-detection plumbing actually works against the live UI; the
/// stage transitions and event types a firing trigger is configured to produce are listed
/// on its <see cref="TriggerCheckResult"/> for review, not separately re-verified here.
///
/// Pure Application-layer orchestration: never references
/// <c>System.Windows.Automation.AutomationElement</c> or any other Prescriva.Agent.Windows
/// type, and is fully testable with fakes for all three consumed interfaces.
/// </summary>
public sealed class IntegrationTestRunner
{
    private static readonly TimeSpan DefaultTriggerTimeout = TimeSpan.FromSeconds(10);

    private readonly ISelectorResolver _resolver;
    private readonly ICaptureProvider _captureProvider;
    private readonly ITriggerProvider _triggerProvider;
    private readonly TimeSpan _triggerTimeout;
    private readonly Func<DateTimeOffset> _clock;

    public IntegrationTestRunner(
        ISelectorResolver resolver,
        ICaptureProvider captureProvider,
        ITriggerProvider triggerProvider,
        TimeSpan? triggerTimeout = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(captureProvider);
        ArgumentNullException.ThrowIfNull(triggerProvider);

        _resolver = resolver;
        _captureProvider = captureProvider;
        _triggerProvider = triggerProvider;
        _triggerTimeout = triggerTimeout ?? DefaultTriggerTimeout;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IntegrationTestReport> RunAsync(
        IntegrationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var fieldResults = ImmutableArray.CreateBuilder<FieldCheckResult>();
        if (!configuration.Fields.IsDefault)
        {
            foreach (var field in configuration.Fields)
            {
                fieldResults.Add(await CheckFieldAsync(field, cancellationToken).ConfigureAwait(false));
            }
        }

        var triggerResults = ImmutableArray.CreateBuilder<TriggerCheckResult>();
        if (!configuration.Triggers.IsDefault)
        {
            foreach (var trigger in configuration.Triggers)
            {
                triggerResults.Add(await CheckTriggerAsync(trigger, cancellationToken).ConfigureAwait(false));
            }
        }

        var fingerprint = ConfigurationFingerprint.Compute(configuration);
        return new IntegrationTestReport(
            configuration.Id,
            fingerprint,
            _clock(),
            fieldResults.ToImmutable(),
            triggerResults.ToImmutable());
    }

    private async Task<FieldCheckResult> CheckFieldAsync(FieldDefinition field, CancellationToken cancellationToken)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = await CheckFieldCoreAsync(field, cancellationToken).ConfigureAwait(false);
        return result with { Duration = elapsed.Elapsed };
    }

    private async Task<FieldCheckResult> CheckFieldCoreAsync(FieldDefinition field, CancellationToken cancellationToken)
    {
        var resolution = await _resolver.ResolveAsync(field.Selector, cancellationToken).ConfigureAwait(false);
        var signals = resolution.Evidence;
        var warnings = SelectorFragility.Assess(field.Selector, resolution);

        if (resolution.Status == SelectorResolutionStatus.Found && resolution.Handle is not null)
        {
            var capture = await _captureProvider.CaptureAsync(resolution.Handle, field, cancellationToken).ConfigureAwait(false);
            var readable = capture.Outcome == CaptureOutcome.Captured &&
                (field.Kind != FieldKind.File || capture.Attachment is not null);
            return readable
                ? new FieldCheckResult(field.Id, FieldCheckOutcome.Found, capture.ProviderId, capture.Confidence, FailureCode: null, Value: capture.Value, Attachment: capture.Attachment, CaptureOutcome: capture.Outcome, Signals: signals, Lead: resolution.Lead, Warnings: warnings)
                : new FieldCheckResult(field.Id, FieldCheckOutcome.Unreadable, capture.ProviderId, resolution.Confidence, FieldCheckResult.FieldUnreadableCode, CaptureOutcome: capture.Outcome, Signals: signals, Lead: resolution.Lead, Warnings: warnings);
        }

        var outcome = resolution.Status == SelectorResolutionStatus.Ambiguous
            ? FieldCheckOutcome.Ambiguous
            : FieldCheckOutcome.NotFound;
        var failureCode = outcome == FieldCheckOutcome.Ambiguous
            ? FieldCheckResult.FieldAmbiguousCode
            : FieldCheckResult.FieldNotFoundCode;

        return new FieldCheckResult(field.Id, outcome, ProviderId: null, resolution.Confidence, failureCode, Signals: signals, Warnings: []);
    }

    private async Task<TriggerCheckResult> CheckTriggerAsync(TriggerDefinition trigger, CancellationToken cancellationToken)
    {
        var transitions = trigger.Actions.IsDefault
            ? ImmutableArray<string>.Empty
            : trigger.Actions.OfType<TransitionStageAction>().Select(action => action.StageId).ToImmutableArray();
        var events = trigger.Actions.IsDefault
            ? ImmutableArray<string>.Empty
            : trigger.Actions.OfType<EmitEventAction>().Select(action => action.EventType).ToImmutableArray();

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var detected = await WaitForTriggerAsync(trigger, cancellationToken).ConfigureAwait(false);

        return new TriggerCheckResult(
            trigger.Id,
            detected ? TriggerCheckOutcome.Detected : TriggerCheckOutcome.TimedOut,
            transitions,
            events,
            detected ? null : TriggerCheckResult.TriggerTimedOutCode,
            elapsed.Elapsed);
    }

    private async Task<bool> WaitForTriggerAsync(TriggerDefinition trigger, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_triggerTimeout);

        try
        {
            await foreach (var _ in _triggerProvider.WatchAsync(trigger, timeoutCts.Token)
                .WithCancellation(timeoutCts.Token).ConfigureAwait(false))
            {
                return true;
            }

            // The provider completed its enumeration on its own (e.g. a fake yielding a
            // fixed sequence in tests) without ever producing a signal.
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the trigger-timeout budget elapsed, not the caller's own cancellation -
            // this is the ordinary "never fired within the wait window" outcome, not a
            // failure the caller needs to see as an exception.
            return false;
        }
    }
}
