using System;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Desktop.Inspection;

/// <summary>
/// Wires an <see cref="InspectionController"/>'s state into something a WPF view can
/// bind to. Holds no UI Automation reference and no XAML dependency of its own - it is
/// plain INotifyPropertyChanged over InspectionController's already-plain
/// <see cref="InspectionState"/>.
///
/// InspectionController raises <see cref="InspectionController.StateChanged"/> from
/// whatever thread the triggering call (e.g. ObservePointerAsync's async continuation)
/// happens to resume on, which is not guaranteed to be the UI thread. This view model
/// marshals every update onto the supplied <see cref="Dispatcher"/> before touching its
/// own properties, so a bound view is never updated off-thread.
/// </summary>
public sealed class InspectorViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly InspectionController _controller;
    private readonly Dispatcher _dispatcher;
    private InspectionState _state;

    public InspectorViewModel(InspectionController controller, Dispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(controller);

        _controller = controller;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _state = controller.CurrentState;

        _controller.StateChanged += OnControllerStateChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The full, immutable state as last reported by the controller.</summary>
    public InspectionState State => _state;

    public InspectionSessionStatus Status => _state.Status;

    public ElementSnapshot? Snapshot => _state.Snapshot;

    public BoundingRectangle? Bounds => _state.Bounds;

    public ElementFingerprint? Fingerprint => _state.Fingerprint;

    public ImmutableArray<string> Warnings => _state.Warnings;

    /// <summary>True while a session is active and there is currently something to highlight.</summary>
    public bool IsHighlightVisible =>
        _state.Status == InspectionSessionStatus.Active && _state.Bounds is not null;

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _controller.StartAsync(cancellationToken);

    public Task ObservePointerAsync(ScreenPoint point, CancellationToken cancellationToken = default) =>
        _controller.ObservePointerAsync(point, cancellationToken);

    /// <summary>Observes the pointer at <paramref name="depth"/> (Shift held: the innermost element).</summary>
    public Task ObservePointerAsync(ScreenPoint point, InspectionDepth depth, CancellationToken cancellationToken = default) =>
        _controller.ObservePointerAsync(point, depth, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        _controller.StopAsync(cancellationToken);

    public Task<InspectionState> ConfirmAsync(CancellationToken cancellationToken = default) =>
        _controller.ConfirmAsync(cancellationToken);

    public void Dispose()
    {
        _controller.StateChanged -= OnControllerStateChanged;
    }

    private void OnControllerStateChanged(object? sender, InspectionState state)
    {
        if (_dispatcher.CheckAccess())
        {
            ApplyState(state);
        }
        else
        {
            _dispatcher.BeginInvoke(() => ApplyState(state));
        }
    }

    private void ApplyState(InspectionState state)
    {
        _state = state;

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(Bounds));
        OnPropertyChanged(nameof(Fingerprint));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(IsHighlightVisible));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
