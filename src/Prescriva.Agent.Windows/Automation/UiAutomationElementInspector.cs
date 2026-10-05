using System.Diagnostics;
using System.Linq;
using System.Windows.Automation;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Implements <see cref="IElementInspector"/> using real `System.Windows.Automation`
/// calls, always marshaled through an <see cref="AutomationDispatcher"/>. This is the
/// only class in the solution (besides the dispatcher itself) that is meant to touch a
/// live `AutomationElement` - everything it returns is a plain, disconnected snapshot.
/// </summary>
public sealed class UiAutomationElementInspector : IElementInspector, IDisposable
{
    private readonly AutomationDispatcher _dispatcher;
    private readonly bool _ownsDispatcher;

    public UiAutomationElementInspector()
        : this(new AutomationDispatcher(), ownsDispatcher: true)
    {
    }

    public UiAutomationElementInspector(AutomationDispatcher dispatcher)
        : this(dispatcher, ownsDispatcher: false)
    {
    }

    private UiAutomationElementInspector(AutomationDispatcher dispatcher, bool ownsDispatcher)
    {
        _dispatcher = dispatcher;
        _ownsDispatcher = ownsDispatcher;
    }

    public async Task<InspectionResult> FromPointAsync(ScreenPoint point, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _dispatcher.RunAsync(
                _ =>
                {
                    AutomationElement? element;
                    try
                    {
                        element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
                    }
                    catch (ElementNotAvailableException ex)
                    {
                        throw new ElementInspectionFailure(ElementInspectionFailureKind.WindowMissing, "No element is available at the given point.", ex);
                    }

                    if (element is null)
                    {
                        throw new ElementInspectionFailure(ElementInspectionFailureKind.WindowMissing, "No element was found at the given point.");
                    }

                    return CreateSnapshot(PromoteToInteractiveAncestor(element));
                },
                timeout,
                cancellationToken)
                .ConfigureAwait(false);

            return InspectionResult.Found(snapshot);
        }
        catch (ElementInspectionFailure failure) when (failure.Kind == ElementInspectionFailureKind.Cancelled)
        {
            throw ToOperationCanceledException(failure, cancellationToken);
        }
        catch (ElementInspectionFailure failure)
        {
            return failure.Kind switch
            {
                ElementInspectionFailureKind.TimedOut => InspectionResult.TimedOut(),
                ElementInspectionFailureKind.WindowMissing => InspectionResult.WindowMissing(failure.Message),
                _ => InspectionResult.ElementUnavailable(failure.Message),
            };
        }
    }

    public async Task<IReadOnlyList<ElementSnapshot>> FindCandidatesAsync(
        ApplicationDefinition application,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);

        try
        {
            return await _dispatcher.RunAsync(
                _ =>
                {
                    var window = AutomationWindowLocator.Find(application.ProcessIdentity, application.WindowRule)
                        ?? throw new ElementInspectionFailure(
                            ElementInspectionFailureKind.WindowMissing,
                            $"No window found for process '{application.ProcessIdentity}' matching window rule '{application.WindowRule}'.");

                    var descendants = AutomationWindowLocator.FindDescendants(window);
                    var snapshots = descendants.Select(CreateSnapshot).ToList();

                    return (IReadOnlyList<ElementSnapshot>)snapshots;
                },
                timeout,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ElementInspectionFailure failure) when (failure.Kind == ElementInspectionFailureKind.Cancelled)
        {
            // Mirror FromPointAsync: a caller following the normal .NET cancellation
            // convention around this method must see OperationCanceledException, not a
            // generic typed failure, when its own token caused the cancellation.
            throw ToOperationCanceledException(failure, cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_ownsDispatcher)
        {
            _dispatcher.Dispose();
        }
    }

    private static OperationCanceledException ToOperationCanceledException(ElementInspectionFailure failure, CancellationToken cancellationToken) =>
        new(failure.Message, failure, cancellationToken);

    private static readonly ControlType[] CaptionControlTypes = [ControlType.Text, ControlType.Image];

    private static readonly ControlType[] InteractiveControlTypes =
    [
        ControlType.Button, ControlType.SplitButton, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.Hyperlink, ControlType.MenuItem, ControlType.TabItem, ControlType.ListItem,
        ControlType.ComboBox, ControlType.TreeItem,
    ];

    private const int MaxCaptionDepth = 3;

    /// <summary>
    /// UI Automation hit-tests the innermost element, so pointing at a button's centre
    /// returns its caption (an anonymous Text or Image with no AutomationId) rather than
    /// the button. When the hit element is such an anonymous caption and an interactive
    /// control contains it within a few levels, the operator means that control. A
    /// stand-alone label, or any element with its own AutomationId, is returned as is.
    /// </summary>
    private static AutomationElement PromoteToInteractiveAncestor(AutomationElement element)
    {
        try
        {
            var current = element.Current;
            if (!string.IsNullOrEmpty(current.AutomationId) || !CaptionControlTypes.Contains(current.ControlType))
            {
                return element;
            }

            var walker = TreeWalker.RawViewWalker;
            var candidate = element;
            for (var depth = 0; depth < MaxCaptionDepth; depth++)
            {
                candidate = walker.GetParent(candidate);
                if (candidate is null || System.Windows.Automation.Automation.Compare(candidate, AutomationElement.RootElement))
                {
                    break;
                }

                if (InteractiveControlTypes.Contains(candidate.Current.ControlType))
                {
                    return candidate;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            // The element went away mid-walk; report what was hit.
        }

        return element;
    }

    private static ElementSnapshot CreateSnapshot(AutomationElement element)
    {
        var current = element.Current;
        var rect = current.BoundingRectangle;

        string processName;
        try
        {
            processName = Process.GetProcessById(current.ProcessId).ProcessName;
        }
        catch (ArgumentException)
        {
            // The owning process has already exited between the hit-test and this call.
            processName = string.Empty;
        }

        var window = FindTopLevelWindow(element);
        return new ElementSnapshot(
            NullIfEmpty(current.AutomationId),
            NullIfEmpty(current.Name),
            current.ControlType?.ProgrammaticName ?? "ControlType.Unknown",
            NullIfEmpty(current.ClassName),
            new BoundingRectangle(rect.X, rect.Y, rect.Width, rect.Height),
            current.ProcessId,
            processName,
            NullIfEmpty(window?.Current.Name),
            StructuralSignals.Ancestors(element),
            StructuralSignals.NearbyLabels(element),
            window is null ? null : StructuralSignals.Relative(rect, window.Current.BoundingRectangle),
            NullIfEmpty(current.FrameworkId));
    }

    /// <summary>
    /// Walks up the raw tree to the highest ancestor below the desktop root and returns
    /// its name (i.e. the containing top-level window's title), used to populate
    /// <see cref="ElementSnapshot.WindowTitle"/>. Best-effort: returns null rather than
    /// throwing if the element disappears mid-walk.
    /// </summary>
    private static AutomationElement? FindTopLevelWindow(AutomationElement element)
    {
        try
        {
            var walker = TreeWalker.RawViewWalker;
            var current = element;
            var root = AutomationElement.RootElement;

            while (true)
            {
                var parent = walker.GetParent(current);
                if (parent is null || System.Windows.Automation.Automation.Compare(parent, root))
                {
                    return current;
                }

                current = parent;
            }
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
