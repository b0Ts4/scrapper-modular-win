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

                    return CreateSnapshot(element);
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
