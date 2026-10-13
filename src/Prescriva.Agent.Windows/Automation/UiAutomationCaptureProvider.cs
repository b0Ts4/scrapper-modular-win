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

        if (field.Kind.ProducesAttachment())
        {
            return await CaptureFileAsync(uiaHandle, field, cancellationToken).ConfigureAwait(false);
        }

        if (field.Kind == FieldKind.OcrText)
        {
            return await CaptureOcrAsync(uiaHandle, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// A file field: the probe (text or screen image) runs on the dispatcher thread; reading
    /// a file from disk does not, so a large file never blocks UI Automation. A screen-image
    /// field skips the text: it is always the control's on-screen image.
    /// </summary>
    private async Task<CaptureResult> CaptureFileAsync(UiaResolvedElementHandle handle, FieldDefinition field, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var probe = await _dispatcher.RunAsync(
                _ => field.Kind == FieldKind.ScreenImage
                    ? FileFieldCapture.ProbeScreenOnDispatcherThread(handle.Element)
                    : FileFieldCapture.ProbeOnDispatcherThread(handle.Element),
                _timeout,
                cancellationToken).ConfigureAwait(false);

            if (probe.Failure is { } failure)
            {
                return new CaptureResult(failure, null, CaptureResult.UiaProviderId, 0, stopwatch.Elapsed, [new PatternAttempt("File", false, probe.Detail)]);
            }

            if (probe.Png is { } png)
            {
                var attachment = new CapturedAttachment(png, field.Id + ".png", "image/png", AttachmentSource.Screen);
                return png.LongLength > AttachmentLimits.MaxBytes
                    ? new CaptureResult(CaptureOutcome.TooLarge, null, CaptureResult.UiaProviderId, 0, stopwatch.Elapsed, [new PatternAttempt("Screen", false, "too large")])
                    : new CaptureResult(CaptureOutcome.Captured, null, CaptureResult.UiaProviderId, 1.0, stopwatch.Elapsed, [new PatternAttempt("Screen", true)], attachment);
            }

            var (outcome, file) = await FileFieldCapture.ReadFileAsync(probe.Path!, cancellationToken).ConfigureAwait(false);
            return new CaptureResult(outcome, null, CaptureResult.UiaProviderId, outcome == CaptureOutcome.Captured ? 1.0 : 0, stopwatch.Elapsed,
                [new PatternAttempt("File", outcome == CaptureOutcome.Captured)], file);
        }
        catch (ElementInspectionFailure failure) when (failure.Kind == ElementInspectionFailureKind.Cancelled)
        {
            throw new OperationCanceledException(failure.Message, failure, cancellationToken);
        }
        catch (ElementInspectionFailure failure)
        {
            var outcome = failure.Kind == ElementInspectionFailureKind.TimedOut ? CaptureOutcome.TimedOut : CaptureOutcome.ElementUnavailable;
            return new CaptureResult(outcome, null, CaptureResult.UiaProviderId, 0, stopwatch.Elapsed, [new PatternAttempt("File", false, failure.Message)]);
        }
    }

    /// <summary>
    /// An OCR field: the control's image is taken on the dispatcher thread (refused when
    /// covered); recognition runs off it. A control that shows nothing reads as empty text.
    /// </summary>
    private async Task<CaptureResult> CaptureOcrAsync(UiaResolvedElementHandle handle, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var probe = await _dispatcher.RunAsync(
                _ => FileFieldCapture.ProbeScreenOnDispatcherThread(handle.Element),
                _timeout,
                cancellationToken).ConfigureAwait(false);

            if (probe.Failure is CaptureOutcome.FileUnavailable)
            {
                return new CaptureResult(CaptureOutcome.Captured, string.Empty, OcrFieldCapture.ProviderId, 1.0, stopwatch.Elapsed, [new PatternAttempt("ocr", true, probe.Detail)]);
            }

            if (probe.Failure is { } failure)
            {
                return new CaptureResult(failure, null, OcrFieldCapture.ProviderId, 0, stopwatch.Elapsed, [new PatternAttempt("ocr", false, probe.Detail)]);
            }

            var reading = await OcrFieldCapture.RecognizeAsync(probe.Png!, cancellationToken).ConfigureAwait(false);
            return reading.Language is null
                ? new CaptureResult(CaptureOutcome.OcrUnavailable, null, OcrFieldCapture.ProviderId, 0, stopwatch.Elapsed, [new PatternAttempt("ocr", false, "No OCR recognizer language is installed.")])
                : new CaptureResult(CaptureOutcome.Captured, reading.Text, OcrFieldCapture.ProviderId, 1.0, stopwatch.Elapsed, [new PatternAttempt("ocr:" + reading.Language, true)]);
        }
        catch (ElementInspectionFailure failure) when (failure.Kind == ElementInspectionFailureKind.Cancelled)
        {
            throw new OperationCanceledException(failure.Message, failure, cancellationToken);
        }
        catch (ElementInspectionFailure failure)
        {
            var outcome = failure.Kind == ElementInspectionFailureKind.TimedOut ? CaptureOutcome.TimedOut : CaptureOutcome.ElementUnavailable;
            return new CaptureResult(outcome, null, OcrFieldCapture.ProviderId, 0, stopwatch.Elapsed, [new PatternAttempt("ocr", false, failure.Message)]);
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

            if (TryCaptureViaLabelName(element, attempts, out var labelResult))
            {
                return (labelResult, attempts);
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

    /// <summary>
    /// A plain label (ControlType.Text) - a calculator display, an ERP total - often exposes no
    /// pattern at all: its text is its Name. Only labels: a button's Name is its caption, not a value.
    /// </summary>
    private static bool TryCaptureViaLabelName(AutomationElement element, List<PatternAttempt> attempts, out string? value)
    {
        if (element.Current.ControlType == ControlType.Text)
        {
            value = element.Current.Name;
            attempts.Add(new PatternAttempt("Name", Succeeded: true));
            return true;
        }

        attempts.Add(new PatternAttempt("Name", Succeeded: false, "Only a label (ControlType.Text) is read through its Name."));
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
