using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Diagnostics;
using Prescriva.Agent.Application.Events;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Runtime;

/// <summary>
/// Builds the <see cref="ITriggerProvider"/> that serves one application instance's session.
/// Receives the <see cref="SessionCoordinator"/>'s own <paramref name="sessionId"/> (not just
/// the <see cref="ApplicationInstance"/>) because a real provider - e.g.
/// <c>UiAutomationTriggerProvider</c> - stamps that session ID onto every
/// <see cref="TriggerSignal"/> it yields, so the two must agree.
/// </summary>
public delegate ITriggerProvider TriggerProviderFactory(ApplicationInstance instance, Guid sessionId);

/// <summary>Builds the <see cref="ISelectorResolver"/> that serves one application instance.</summary>
public delegate ISelectorResolver SelectorResolverFactory(ApplicationInstance instance);

/// <summary>Builds the <see cref="ICaptureProvider"/> that serves one application instance.</summary>
public delegate ICaptureProvider CaptureProviderFactory(ApplicationInstance instance);

/// <summary>The outcome of a single <see cref="AgentRuntime.ActivateAsync"/> call.</summary>
public enum ActivationStatus
{
    /// <summary>The configuration was approved for its exact current content; activation ran (and, on normal return, ran until cancelled).</summary>
    Activated,

    /// <summary>
    /// Activation was refused: either the configuration has never been approved, or a
    /// previous approval exists but no longer matches the configuration's current content
    /// hash (it was edited after being tested). No application instances were watched and
    /// no session was started.
    /// </summary>
    NotTested,
}

/// <summary>The result of a single <see cref="AgentRuntime.ActivateAsync"/> call.</summary>
public sealed record ActivationResult(ActivationStatus Status)
{
    public static readonly ActivationResult NotTested = new(ActivationStatus.NotTested);
    public static readonly ActivationResult Activated = new(ActivationStatus.Activated);
}

/// <summary>
/// The top-level orchestrator that wires the trigger provider, selector resolver, capture
/// provider, session engine and event outbox together into one running agent. Watches
/// <see cref="IApplicationInstanceSource"/> for application instances starting and
/// stopping; creates exactly one <see cref="SessionCoordinator"/> (and therefore exactly
/// one <see cref="Domain.Sessions.CaptureSession"/>) per instance, using the supplied
/// factories to build that instance's own trigger provider/resolver/capture provider; and
/// ends that instance's session when it stops.
///
/// Each instance's <see cref="SessionCoordinator.RunAsync"/> runs as its own independent
/// background task - starting one instance never waits for another instance's triggers to
/// settle, which is what keeps two running copies of the same configured application fully
/// independent (per this task's review focus).
///
/// <see cref="ActivateAsync"/> gates on a <see cref="ConfigurationApproval"/> bound to the
/// configuration's exact current content: only when the supplied approval's
/// <see cref="ConfigurationApproval.IsValidFor"/> matches the configuration's current
/// <see cref="ConfigurationFingerprint"/> does activation actually start watching for
/// application instances - "Somente configurações testadas podem ser ativadas". Any other
/// case (no approval, or one that no longer matches because the configuration was edited
/// after it was tested) returns <see cref="ActivationResult.NotTested"/> immediately,
/// without watching anything or starting a single session.
/// </summary>
public sealed class AgentRuntime
{
    private readonly IApplicationInstanceSource _instanceSource;
    private readonly TriggerProviderFactory _triggerProviderFactory;
    private readonly SelectorResolverFactory _selectorResolverFactory;
    private readonly CaptureProviderFactory _captureProviderFactory;
    private readonly IEventOutbox _outbox;
    private readonly ITechnicalLog _log;
    private readonly Func<Guid> _sessionIdFactory;
    private readonly Func<Guid> _eventIdFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IAttachmentStore? _attachments;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveInstance> _active = new();

    public AgentRuntime(
        IApplicationInstanceSource instanceSource,
        TriggerProviderFactory triggerProviderFactory,
        SelectorResolverFactory selectorResolverFactory,
        CaptureProviderFactory captureProviderFactory,
        IEventOutbox outbox,
        ITechnicalLog log,
        Func<Guid>? sessionIdFactory = null,
        Func<Guid>? eventIdFactory = null,
        Func<DateTimeOffset>? clock = null,
        IAttachmentStore? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(instanceSource);
        ArgumentNullException.ThrowIfNull(triggerProviderFactory);
        ArgumentNullException.ThrowIfNull(selectorResolverFactory);
        ArgumentNullException.ThrowIfNull(captureProviderFactory);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(log);

        _instanceSource = instanceSource;
        _triggerProviderFactory = triggerProviderFactory;
        _selectorResolverFactory = selectorResolverFactory;
        _captureProviderFactory = captureProviderFactory;
        _outbox = outbox;
        _log = log;
        _sessionIdFactory = sessionIdFactory ?? Guid.NewGuid;
        _eventIdFactory = eventIdFactory ?? Guid.NewGuid;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _attachments = attachments;
    }

    /// <summary>Raised for every diagnostic any active session's coordinator publishes.</summary>
    public event EventHandler<RuntimeDiagnostic>? DiagnosticPublished;

    /// <summary>The coordinator currently owning each active application instance's session, keyed by instance ID.</summary>
    public IReadOnlyDictionary<Guid, SessionCoordinator> ActiveSessions
    {
        get
        {
            lock (_gate)
            {
                return _active.ToDictionary(pair => pair.Key, pair => pair.Value.Coordinator);
            }
        }
    }

    /// <summary>
    /// Activates <paramref name="configuration"/>: if, and only if, <paramref name="approval"/>
    /// is valid for the configuration's exact current content, watches for its application's
    /// instances and coordinates a capture session for each one until
    /// <paramref name="cancellationToken"/> is cancelled, at which point every still-active
    /// session is closed and awaited before this method returns
    /// <see cref="ActivationResult.Activated"/>.
    ///
    /// When <paramref name="approval"/> is null, or is non-null but
    /// <see cref="ConfigurationApproval.IsValidFor"/> the configuration's current
    /// <see cref="ConfigurationFingerprint"/> returns false (no approval was ever recorded
    /// for this exact content, or the configuration was edited after it was tested),
    /// returns <see cref="ActivationResult.NotTested"/> immediately - no application
    /// instances are watched and no session is ever started.
    /// </summary>
    public async Task<ActivationResult> ActivateAsync(
        IntegrationConfiguration configuration,
        ConfigurationApproval? approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Stages.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Configuration must declare at least one stage.", nameof(configuration));
        }

        var fingerprint = ConfigurationFingerprint.Compute(configuration);
        if (approval is null || !approval.IsValidFor(fingerprint))
        {
            _log.Log(new TechnicalLogEntry(
                TechnicalLogLevel.Warning,
                "activation_not_tested",
                "Activation refused: the configuration has no approval matching its current content (never tested, or edited since it was last tested).",
                _clock()));
            return ActivationResult.NotTested;
        }

        var initialStageId = configuration.Stages[0].Id;

        try
        {
            await foreach (var change in _instanceSource.WatchAsync(configuration.Application, cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (change.Kind == ApplicationInstanceChangeKind.Started)
                {
                    StartInstance(configuration, initialStageId, change.Instance, cancellationToken);
                }
                else
                {
                    await StopInstanceAsync(change.Instance).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelling the activation token is how monitoring is stopped: a normal end,
            // whether the source noticed it inside its polling wait or elsewhere.
        }
        finally
        {
            await CloseAllAsync().ConfigureAwait(false);
        }

        return ActivationResult.Activated;
    }

    private void StartInstance(
        IntegrationConfiguration configuration,
        string initialStageId,
        ApplicationInstance instance,
        CancellationToken outerCancellationToken)
    {
        var sessionId = _sessionIdFactory();
        var coordinator = new SessionCoordinator(
            sessionId,
            initialStageId,
            configuration,
            _triggerProviderFactory(instance, sessionId),
            _selectorResolverFactory(instance),
            _captureProviderFactory(instance),
            _outbox,
            _log,
            _eventIdFactory,
            _clock,
            attachments: _attachments);
        coordinator.DiagnosticPublished += OnDiagnosticPublished;

        var instanceCts = CancellationTokenSource.CreateLinkedTokenSource(outerCancellationToken);
        var runTask = coordinator.RunAsync(instanceCts.Token);

        lock (_gate)
        {
            _active[instance.InstanceId] = new ActiveInstance(coordinator, runTask, instanceCts);
        }
    }

    private async Task StopInstanceAsync(ApplicationInstance instance)
    {
        ActiveInstance? found = null;
        lock (_gate)
        {
            if (_active.Remove(instance.InstanceId, out var existing))
            {
                found = existing;
            }
        }

        if (found is { } active)
        {
            await EndInstanceAsync(active).ConfigureAwait(false);
        }
    }

    private async Task CloseAllAsync()
    {
        List<ActiveInstance> remaining;
        lock (_gate)
        {
            remaining = _active.Values.ToList();
            _active.Clear();
        }

        foreach (var active in remaining)
        {
            await EndInstanceAsync(active).ConfigureAwait(false);
        }
    }

    private static async Task EndInstanceAsync(ActiveInstance active)
    {
        await active.Coordinator.CloseAsync().ConfigureAwait(false);
        active.Cts.Cancel();
        try
        {
            await active.RunTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: cancelling the instance's linked token stops its trigger watches.
        }
        finally
        {
            active.Cts.Dispose();
        }
    }

    private void OnDiagnosticPublished(object? sender, RuntimeDiagnostic diagnostic) =>
        DiagnosticPublished?.Invoke(this, diagnostic);

    private sealed record ActiveInstance(SessionCoordinator Coordinator, Task RunTask, CancellationTokenSource Cts);
}

