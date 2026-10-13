using System.Globalization;
using System.Windows.Automation;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Implements <see cref="ISelectorResolver"/> by combining real UI Automation
/// enumeration (marshaled through <see cref="AutomationDispatcher"/>, same discipline as
/// <see cref="UiAutomationElementInspector"/>) with <see cref="SelectorMatcher"/>'s
/// deterministic scoring. This is one of the few classes in the solution meant to touch a
/// live <see cref="AutomationElement"/> - the only thing it returns to callers is an
/// opaque <see cref="ResolvedElementHandle"/>.
/// </summary>
public sealed class UiAutomationSelectorResolver : ISelectorResolver, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly AutomationDispatcher _dispatcher;
    private readonly bool _ownsDispatcher;
    private readonly SelectorMatcher _matcher = new();
    private readonly SelectorWeights _weights;
    private readonly TimeSpan _timeout;
    private readonly int? _processId;

    public UiAutomationSelectorResolver(SelectorWeights? weights = null, TimeSpan? timeout = null, int? processId = null)
        : this(new AutomationDispatcher(), ownsDispatcher: true, weights, timeout, processId)
    {
    }

    public UiAutomationSelectorResolver(
        AutomationDispatcher dispatcher,
        SelectorWeights? weights = null,
        TimeSpan? timeout = null,
        int? processId = null)
        : this(dispatcher, ownsDispatcher: false, weights, timeout, processId)
    {
    }

    private UiAutomationSelectorResolver(
        AutomationDispatcher dispatcher,
        bool ownsDispatcher,
        SelectorWeights? weights,
        TimeSpan? timeout,
        int? processId)
    {
        _dispatcher = dispatcher;
        _ownsDispatcher = ownsDispatcher;
        _weights = weights ?? new SelectorWeights();
        _timeout = timeout ?? DefaultTimeout;
        _processId = processId;
    }

    public async Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        try
        {
            return await _dispatcher.RunAsync(
                _ => ResolveOnDispatcherThread(fingerprint),
                _timeout,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ElementInspectionFailure failure) when (failure.Kind == ElementInspectionFailureKind.Cancelled)
        {
            throw new OperationCanceledException(failure.Message, failure, cancellationToken);
        }
        catch (ElementInspectionFailure failure)
        {
            return failure.Kind switch
            {
                ElementInspectionFailureKind.TimedOut => SelectorResolution.TimedOut(),
                ElementInspectionFailureKind.WindowMissing => SelectorResolution.WindowMissing(failure.Message),
                _ => SelectorResolution.NotFound(failure.Message),
            };
        }
    }

    public void Dispose()
    {
        if (_ownsDispatcher)
        {
            _dispatcher.Dispose();
        }
    }

    private SelectorResolution ResolveOnDispatcherThread(ElementFingerprint fingerprint)
    {
        var window = AutomationWindowLocator.Find(fingerprint.ProcessIdentity, fingerprint.WindowRule, _processId)
            ?? throw new ElementInspectionFailure(
                ElementInspectionFailureKind.WindowMissing,
                $"No window found for process '{fingerprint.ProcessIdentity}' matching window rule '{fingerprint.WindowRule}'.");

        if (string.IsNullOrWhiteSpace(fingerprint.AutomationId) && !string.IsNullOrWhiteSpace(fingerprint.WindowPath))
        {
            return ResolveByWindowPath(fingerprint, window);
        }

        var descendants = AutomationWindowLocator.FindDescendants(window);
        var windowBounds = window.Current.BoundingRectangle;

        var elementsById = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
        var candidates = new List<ElementCandidate>(descendants.Count);
        for (var index = 0; index < descendants.Count; index++)
        {
            var descendant = descendants[index];
            var id = index.ToString(CultureInfo.InvariantCulture);
            elementsById[id] = descendant;
            candidates.Add(new ElementCandidate(id, CreateCandidateFingerprint(fingerprint, descendant, windowBounds)));
        }

        var match = _matcher.Match(fingerprint, candidates, _weights);
        return match.Status switch
        {
            SelectorMatchStatus.Found => SelectorResolution.Found(
                new UiaResolvedElementHandle(elementsById[match.CandidateId!]),
                match.Confidence,
                match.Evidence,
                match.RunnerUpScore is { } runnerUp ? match.Score - runnerUp : null),
            SelectorMatchStatus.Ambiguous => SelectorResolution.Ambiguous(match.Confidence),
            _ => SelectorResolution.NotFound("No matching element was found."),
        };
    }

    /// <summary>
    /// A Delphi control (plan 14): its child windows are enumerated natively - walking the UI
    /// Automation tree of such a form took ~28 s on a real pharmacy system, the native
    /// enumeration milliseconds - and only the winner is wrapped for UI Automation. A candidate
    /// of the selector's window class is taken to have the selector's control type (UI
    /// Automation derives it from the class); its caption is read only when the selector has a
    /// name (a button), never an edit's text.
    /// </summary>
    private SelectorResolution ResolveByWindowPath(ElementFingerprint fingerprint, AutomationElement window)
    {
        var topLevel = new IntPtr(window.Current.NativeWindowHandle);
        var windowBounds = Win32WindowPath.Bounds(topLevel);
        var descendants = Win32WindowPath.Descendants(topLevel);

        var candidates = new List<ElementCandidate>(descendants.Count);
        for (var index = 0; index < descendants.Count; index++)
        {
            var descendant = descendants[index];
            var sameClass = string.Equals(descendant.ClassName, fingerprint.ClassName, StringComparison.Ordinal);
            candidates.Add(new ElementCandidate(
                index.ToString(CultureInfo.InvariantCulture),
                new ElementFingerprint(
                    fingerprint.ProcessIdentity,
                    fingerprint.WindowRule,
                    ControlType: sameClass ? fingerprint.ControlType : null,
                    Name: sameClass && fingerprint.Name is not null ? NullIfEmpty(Win32WindowPath.Text(descendant.Handle)) : null,
                    ClassName: descendant.ClassName,
                    FrameworkId: sameClass ? fingerprint.FrameworkId : null,
                    RelativeBounds: Relative(Win32WindowPath.Bounds(descendant.Handle), windowBounds),
                    WindowPath: descendant.Path)));
        }

        var match = _matcher.Match(fingerprint, candidates, _weights);
        if (match.Status != SelectorMatchStatus.Found)
        {
            return match.Status == SelectorMatchStatus.Ambiguous
                ? SelectorResolution.Ambiguous(match.Confidence)
                : SelectorResolution.NotFound("No matching element was found.");
        }

        var winner = descendants[int.Parse(match.CandidateId!, CultureInfo.InvariantCulture)];
        return SelectorResolution.Found(
            new UiaResolvedElementHandle(AutomationElement.FromHandle(winner.Handle)),
            match.Confidence,
            match.Evidence,
            match.RunnerUpScore is { } runnerUp ? match.Score - runnerUp : null);
    }

    private static RelativeBounds? Relative(
        (double X, double Y, double Width, double Height)? element,
        (double X, double Y, double Width, double Height)? window) =>
        element is { } e && window is { } w
            ? StructuralSignals.Relative(new System.Windows.Rect(e.X, e.Y, e.Width, e.Height), new System.Windows.Rect(w.X, w.Y, w.Width, w.Height))
            : null;

    /// <summary>
    /// A candidate's fingerprint. Structural signals cost extra UI Automation calls per
    /// element, so each is computed only when the selector itself carries that signal and
    /// the candidate is of the selector's control type (no other candidate could win).
    /// </summary>
    private static ElementFingerprint CreateCandidateFingerprint(ElementFingerprint selector, AutomationElement element, System.Windows.Rect windowBounds)
    {
        var current = element.Current;
        var controlType = current.ControlType?.ProgrammaticName;
        var worthStructure = string.IsNullOrWhiteSpace(selector.ControlType) ||
            string.Equals(selector.ControlType, controlType, StringComparison.OrdinalIgnoreCase);

        return new ElementFingerprint(
            selector.ProcessIdentity,
            selector.WindowRule,
            NullIfEmpty(current.AutomationId),
            NullIfEmpty(current.Name),
            controlType,
            NullIfEmpty(current.ClassName),
            NullIfEmpty(current.FrameworkId),
            worthStructure && !selector.Ancestors.IsDefaultOrEmpty ? StructuralSignals.Ancestors(element) : default,
            worthStructure && !selector.NearbyLabels.IsDefaultOrEmpty ? StructuralSignals.NearbyLabels(element) : default,
            worthStructure && selector.RelativeBounds is not null ? StructuralSignals.Relative(current.BoundingRectangle, windowBounds) : null);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
