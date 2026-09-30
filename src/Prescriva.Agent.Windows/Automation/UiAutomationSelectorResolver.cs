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

        var descendants = AutomationWindowLocator.FindDescendants(window);

        var elementsById = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
        var candidates = new List<ElementCandidate>(descendants.Count);
        for (var index = 0; index < descendants.Count; index++)
        {
            var descendant = descendants[index];
            var id = index.ToString(CultureInfo.InvariantCulture);
            elementsById[id] = descendant;
            candidates.Add(new ElementCandidate(id, CreateCandidateFingerprint(fingerprint, descendant)));
        }

        var match = _matcher.Match(fingerprint, candidates, _weights);
        return match.Status switch
        {
            SelectorMatchStatus.Found => SelectorResolution.Found(
                new UiaResolvedElementHandle(elementsById[match.CandidateId!]),
                match.Confidence),
            SelectorMatchStatus.Ambiguous => SelectorResolution.Ambiguous(match.Confidence),
            _ => SelectorResolution.NotFound("No matching element was found."),
        };
    }

    private static ElementFingerprint CreateCandidateFingerprint(ElementFingerprint selector, AutomationElement element)
    {
        var current = element.Current;
        return new ElementFingerprint(
            selector.ProcessIdentity,
            selector.WindowRule,
            NullIfEmpty(current.AutomationId),
            NullIfEmpty(current.Name),
            current.ControlType?.ProgrammaticName,
            NullIfEmpty(current.ClassName),
            NullIfEmpty(current.FrameworkId));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
