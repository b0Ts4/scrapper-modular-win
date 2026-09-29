using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Implements <see cref="ICaptureProvider"/> using real `System.Windows.Automation`
/// pattern access, always marshaled through an <see cref="AutomationDispatcher"/>. Given
/// an opaque <see cref="ResolvedElementHandle"/> produced by
/// <see cref="UiAutomationSelectorResolver"/>, tries a fixed, ordered sequence of
/// compatible patterns - ValuePattern, then TextPattern, then SelectionPattern - so a
/// control without ValuePattern still yields a value where a compatible pattern exists,
/// and records every attempt so callers can see exactly what was tried.
/// </summary>
public sealed class UiAutomationCaptureProvider : ICaptureProvider, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly AutomationDispatcher _dispatcher;
    private readonly bool _ownsDispatcher;
    private readonly TimeSpan _timeout;

    public UiAutomationCaptureProvider(TimeSpan? timeout = null)
        : this(new AutomationDispatcher(), ownsDispatcher: true, timeout)
    {
    }

    public UiAutomationCaptureProvider(AutomationDispatcher dispatcher, TimeSpan? timeout = null)
        : this(dispatcher, ownsDispatcher: false, timeout)
    {
    }

    private UiAutomationCaptureProvider(AutomationDispatcher dispatcher, bool ownsDispatcher, TimeSpan? timeout)
    {
        _dispatcher = dispatcher;
        _ownsDispatcher = ownsDispatcher;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<CaptureResult> CaptureAsync(
        ResolvedElementHandle handle,
        FieldDefinition field,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(field);

        if (handle is not UiaResolvedElementHandle uiaHandle)
        {
            throw new ArgumentException(
                $"'{nameof(handle)}' was not produced by {nameof(UiAutomationSelectorResolver)}.",
                nameof(handle));
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var (value, attempts) = await _dispatcher.RunAsync(
                _ => CaptureOnDispatcherThread(uiaHandle.Element),
                _timeout,
                cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();

            return value is not null
                ? new CaptureResult(CaptureOutcome.Captured, value, CaptureResult.UiaProviderId, 1.0, stopwatch.Elapsed, attempts)
                : new CaptureResult(CaptureOutcome.UnsupportedPattern, null, CaptureResult.UiaProviderId, 0, stopwatch.Elapsed, attempts);
        }
        catch (ElementInspectionFailure failure) when (failure.Kind == ElementInspectionFailureKind.Cancelled)
        {
            throw new OperationCanceledException(failure.Message, failure, cancellationToken);
        }
        catch (ElementInspectionFailure failure)
        {
            stopwatch.Stop();
            var outcome = failure.Kind == ElementInspectionFailureKind.TimedOut
                ? CaptureOutcome.TimedOut
                : CaptureOutcome.ElementUnavailable;
            var attempts = new[] { new PatternAttempt("n/a", Succeeded: false, failure.Message) };
            return new CaptureResult(outcome, null, CaptureResult.UiaProviderId, 0, stopwatch.Elapsed, attempts);
        }
    }

    public void Dispose()
    {
        if (_ownsDispatcher)
        {
            _dispatcher.Dispose();
        }
    }

    private static (string? Value, IReadOnlyList<PatternAttempt> Attempts) CaptureOnDispatcherThread(AutomationElement element)
    {
        try
        {
            // Force a live round-trip to the element's provider before trying any
            // pattern. TryGetCurrentPattern alone can swallow a dead/unreachable
            // provider and simply report "pattern not supported" instead of raising
            // ElementNotAvailableException, which would misreport a destroyed element
            // as merely lacking compatible patterns.
            _ = element.Current.ControlType;

            var attempts = new List<PatternAttempt>();

            if (TryCaptureViaValuePattern(element, attempts, out var valueResult))
            {
                return (valueResult, attempts);
            }

            if (TryCaptureViaTextPattern(element, attempts, out var textResult))
            {
                return (textResult, attempts);
            }

            if (TryCaptureViaSelectionPattern(element, attempts, out var selectionResult))
            {
                return (selectionResult, attempts);
            }

            return (null, attempts);
        }
        catch (ElementNotAvailableException ex)
        {
            throw new ElementInspectionFailure(
                ElementInspectionFailureKind.ElementUnavailable,
                "The element became unavailable while attempting to capture its value.",
                ex);
        }
    }

    private static bool TryCaptureViaValuePattern(AutomationElement element, List<PatternAttempt> attempts, out string? value)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObject) && patternObject is ValuePattern pattern)
        {
            value = pattern.Current.Value;
            attempts.Add(new PatternAttempt("ValuePattern", Succeeded: true));
            return true;
        }

        attempts.Add(new PatternAttempt("ValuePattern", Succeeded: false, "The element does not support ValuePattern."));
        value = null;
        return false;
    }

    private static bool TryCaptureViaTextPattern(AutomationElement element, List<PatternAttempt> attempts, out string? value)
    {
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) && patternObject is TextPattern pattern)
        {
            value = pattern.DocumentRange.GetText(-1);
            attempts.Add(new PatternAttempt("TextPattern", Succeeded: true));
            return true;
        }

        attempts.Add(new PatternAttempt("TextPattern", Succeeded: false, "The element does not support TextPattern."));
        value = null;
        return false;
    }

    private static bool TryCaptureViaSelectionPattern(AutomationElement element, List<PatternAttempt> attempts, out string? value)
    {
        if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var patternObject) && patternObject is SelectionPattern pattern)
        {
            var selection = pattern.Current.GetSelection();
            if (selection.Length > 0)
            {
                value = selection[0].Current.Name;
                attempts.Add(new PatternAttempt("SelectionPattern", Succeeded: true));
                return true;
            }

            attempts.Add(new PatternAttempt("SelectionPattern", Succeeded: false, "SelectionPattern is supported but nothing is currently selected."));
            value = null;
            return false;
        }

        attempts.Add(new PatternAttempt("SelectionPattern", Succeeded: false, "The element does not support SelectionPattern."));
        value = null;
        return false;
    }
}
