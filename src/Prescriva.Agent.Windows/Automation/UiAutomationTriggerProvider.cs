using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows.Automation;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Implements <see cref="ITriggerProvider"/> using real `System.Windows.Automation`
/// event subscription, always marshaled through an <see cref="AutomationDispatcher"/> -
/// same discipline as <see cref="UiAutomationSelectorResolver"/> and
/// <see cref="UiAutomationCaptureProvider"/>. Resolves the configured element with an
/// internal <see cref="UiAutomationSelectorResolver"/>, then subscribes to its
/// `InvokePattern.InvokedEvent` for the lifetime of the watch. Only plain
/// <see cref="TriggerSignal"/> data crosses back out to the Application layer; the real
/// <see cref="AutomationElement"/> and event plumbing never leave this class.
/// </summary>
public sealed class UiAutomationTriggerProvider : ITriggerProvider, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultLivenessPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly AutomationDispatcher _dispatcher;
    private readonly bool _ownsDispatcher;
    private readonly UiAutomationSelectorResolver _resolver;
    private readonly Guid _sessionId;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _livenessPollInterval;

    public UiAutomationTriggerProvider(
        Guid sessionId,
        TimeSpan? timeout = null,
        TimeSpan? livenessPollInterval = null,
        int? processId = null)
        : this(new AutomationDispatcher(), ownsDispatcher: true, sessionId, timeout, livenessPollInterval, processId)
    {
    }

    public UiAutomationTriggerProvider(
        AutomationDispatcher dispatcher,
        Guid sessionId,
        TimeSpan? timeout = null,
        TimeSpan? livenessPollInterval = null,
        int? processId = null)
        : this(dispatcher, ownsDispatcher: false, sessionId, timeout, livenessPollInterval, processId)
    {
    }

    private UiAutomationTriggerProvider(
        AutomationDispatcher dispatcher,
        bool ownsDispatcher,
        Guid sessionId,
        TimeSpan? timeout,
        TimeSpan? livenessPollInterval,
        int? processId)
    {
        _dispatcher = dispatcher;
        _ownsDispatcher = ownsDispatcher;
        _sessionId = sessionId;
        _timeout = timeout ?? DefaultTimeout;
        _livenessPollInterval = livenessPollInterval ?? DefaultLivenessPollInterval;
        _resolver = new UiAutomationSelectorResolver(dispatcher, processId: processId);
    }

    /// <inheritdoc />
    public event EventHandler<string>? WatchEstablished;

    public async IAsyncEnumerable<TriggerSignal> WatchAsync(
        TriggerDefinition trigger,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        // Only "Invoke" is understood today (buttons via InvokePattern). Any other
        // configured event is a configuration problem this provider cannot act on -
        // surfacing it immediately, rather than silently watching nothing, matches how
        // ConfigurationValidator already requires ObservedEvent to be non-empty.
        if (!string.Equals(trigger.ObservedEvent, "Invoke", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Observed event '{trigger.ObservedEvent}' on trigger '{trigger.Id}' is not supported by " +
                $"{nameof(UiAutomationTriggerProvider)}. Only 'Invoke' is currently supported.");
        }

        var resolution = await _resolver.ResolveAsync(trigger.Selector, cancellationToken).ConfigureAwait(false);
        if (resolution.Status != SelectorResolutionStatus.Found || resolution.Handle is not UiaResolvedElementHandle uiaHandle)
        {
            throw new ElementInspectionFailure(
                resolution.Status switch
                {
                    SelectorResolutionStatus.WindowMissing => ElementInspectionFailureKind.WindowMissing,
                    SelectorResolutionStatus.TimedOut => ElementInspectionFailureKind.TimedOut,
                    _ => ElementInspectionFailureKind.ElementUnavailable,
                },
                $"Could not resolve the element for trigger '{trigger.Id}' (selector resolution status: {resolution.Status}).");
        }

        var element = uiaHandle.Element;
        var channel = Channel.CreateUnbounded<TriggerSignal>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // Deliberately does not touch `element` or `sender`/`args` beyond what is already
        // known from the closure: the callback can run on whatever thread UI Automation's
        // event infrastructure chooses, and only the dispatcher's dedicated thread may
        // ever access a live AutomationElement's members.
        AutomationEventHandler handler = (_, _) =>
            channel.Writer.TryWrite(new TriggerSignal(_sessionId, trigger.Id, DateTimeOffset.UtcNow));

        try
        {
            await _dispatcher.RunAsync(
                _ =>
                {
                    System.Windows.Automation.Automation.AddAutomationEventHandler(
                        InvokePattern.InvokedEvent, element, TreeScope.Element, handler);
                    return true;
                },
                _timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ElementInspectionFailure failure) when (failure.Kind == ElementInspectionFailureKind.Cancelled)
        {
            // Follow the same convention as UiAutomationSelectorResolver.ResolveAsync:
            // cancellation surfaces as OperationCanceledException, not as the typed
            // failure AutomationDispatcher.RunAsync uses internally.
            throw new OperationCanceledException(failure.Message, failure, cancellationToken);
        }

        WatchEstablished?.Invoke(this, trigger.Id);

        using var livenessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var livenessTask = MonitorLivenessAsync(element, channel, livenessCts.Token);

        try
        {
            await foreach (var signal in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return signal;
            }
        }
        finally
        {
            // Always unsubscribe, whether the enumeration ended because the caller
            // cancelled, because the element disappeared (liveness monitor completed the
            // channel with a failure), or for any other reason.
            livenessCts.Cancel();
            try
            {
                await livenessTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // MonitorLivenessAsync already swallows its own cancellation; this is
                // belt-and-suspenders in case a future change lets one through.
            }

            await _dispatcher.RunAsync(
                _ =>
                {
                    System.Windows.Automation.Automation.RemoveAutomationEventHandler(
                        InvokePattern.InvokedEvent, element, handler);
                    return true;
                },
                _timeout,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls the watched element's liveness on the dispatcher thread. If it has become
    /// unavailable (the window or control was destroyed), completes <paramref name="channel"/>
    /// with the typed failure instead of leaving the watch running forever with no way to
    /// observe further invokes.
    /// </summary>
    private async Task MonitorLivenessAsync(
        AutomationElement element,
        Channel<TriggerSignal> channel,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_livenessPollInterval, cancellationToken).ConfigureAwait(false);

                await _dispatcher.RunAsync(
                    innerToken =>
                    {
                        // A cheap, forced live round-trip to the element's provider -
                        // same technique UiAutomationCaptureProvider uses before trying
                        // any pattern. Throws ElementNotAvailableException (translated
                        // by RunAsync into ElementInspectionFailureKind.ElementUnavailable)
                        // once the element - or its window - has gone away.
                        _ = innerToken;
                        _ = element.Current.ControlType;
                        return true;
                    },
                    _timeout,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: the watch was cancelled or is being torn down.
        }
        catch (ElementInspectionFailure failure) when (failure.Kind == ElementInspectionFailureKind.Cancelled)
        {
            // Same shutdown path, just surfaced as a typed failure because it came from
            // inside AutomationDispatcher.RunAsync rather than from Task.Delay.
        }
        catch (ElementInspectionFailure failure)
        {
            channel.Writer.TryComplete(failure);
        }
    }

    public void Dispose()
    {
        _resolver.Dispose();
        if (_ownsDispatcher)
        {
            _dispatcher.Dispose();
        }
    }
}
