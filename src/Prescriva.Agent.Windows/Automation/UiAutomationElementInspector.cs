using System.Diagnostics;
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
                        throw new AutomationFailure(AutomationFailureKind.WindowMissing, "No element is available at the given point.", ex);
                    }

                    if (element is null)
                    {
                        throw new AutomationFailure(AutomationFailureKind.WindowMissing, "No element was found at the given point.");
                    }

                    return CreateSnapshot(element);
                },
                timeout,
                cancellationToken)
                .ConfigureAwait(false);

            return InspectionResult.Found(snapshot);
        }
        catch (AutomationFailure failure)
        {
            return MapToInspectionResult(failure);
        }
    }

    public async Task<IReadOnlyList<ElementSnapshot>> FindCandidatesAsync(
        ApplicationDefinition application,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);

        return await _dispatcher.RunAsync(
            _ =>
            {
                var window = FindApplicationWindow(application)
                    ?? throw new AutomationFailure(
                        AutomationFailureKind.WindowMissing,
                        $"No window found for process '{application.ProcessIdentity}' matching window rule '{application.WindowRule}'.");

                var snapshots = new List<ElementSnapshot>();
                try
                {
                    foreach (AutomationElement descendant in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                    {
                        snapshots.Add(CreateSnapshot(descendant));
                    }
                }
                catch (ElementNotAvailableException ex)
                {
                    throw new AutomationFailure(AutomationFailureKind.WindowMissing, "The target window closed while enumerating its elements.", ex);
                }

                return (IReadOnlyList<ElementSnapshot>)snapshots;
            },
            timeout,
            cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsDispatcher)
        {
            _dispatcher.Dispose();
        }
    }

    private static InspectionResult MapToInspectionResult(AutomationFailure failure) => failure.Kind switch
    {
        AutomationFailureKind.TimedOut => InspectionResult.TimedOut(),
        AutomationFailureKind.WindowMissing => InspectionResult.WindowMissing(failure.Message),
        AutomationFailureKind.Cancelled => throw new OperationCanceledException(failure.Message, failure),
        _ => InspectionResult.ElementUnavailable(failure.Message),
    };

    private static AutomationElement? FindApplicationWindow(ApplicationDefinition application)
    {
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            string processName;
            try
            {
                processName = Process.GetProcessById(window.Current.ProcessId).ProcessName;
            }
            catch (ArgumentException)
            {
                // The process behind this top-level window has already exited.
                continue;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            if (!string.Equals(processName, application.ProcessIdentity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(window.Current.Name, application.WindowRule, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }

        return null;
    }

    private static ElementSnapshot CreateSnapshot(AutomationElement element)
    {
        var current = element.Current;
        var rect = current.BoundingRectangle;

        return new ElementSnapshot(
            NullIfEmpty(current.AutomationId),
            NullIfEmpty(current.Name),
            current.ControlType?.ProgrammaticName ?? "ControlType.Unknown",
            NullIfEmpty(current.ClassName),
            new BoundingRectangle(rect.X, rect.Y, rect.Width, rect.Height));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
