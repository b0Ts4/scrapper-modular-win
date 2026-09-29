using System.Collections.Concurrent;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Owns a single dedicated background STA thread and marshals all UI Automation work
/// onto it. `System.Windows.Automation` is not reliably usable from arbitrary
/// thread-pool threads, so every real `AutomationElement` access in this codebase must
/// go through <see cref="RunAsync{T}"/> rather than touching UIA directly.
///
/// Every operation is bounded by a timeout and a <see cref="CancellationToken"/>. If the
/// caller cancels (or the timeout elapses) before the dispatcher thread reaches an item,
/// that item's operation is never invoked - only work already running on the dispatcher
/// thread can continue past the deadline (UIA offers no reliable way to abort a
/// synchronous COM call in flight), but its result is discarded and the caller still
/// observes the failure promptly.
/// </summary>
public sealed class AutomationDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private volatile bool _disposed;

    public AutomationDispatcher()
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "Prescriva.Agent.Automation",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>The managed thread ID of the dedicated dispatcher thread. Exposed for testing thread affinity.</summary>
    public int ThreadId => _thread.ManagedThreadId;

    /// <summary>
    /// Runs <paramref name="operation"/> on the dispatcher's dedicated STA thread and
    /// returns its result. Throws <see cref="AutomationFailure"/> if the operation is
    /// cancelled, times out, or throws any other exception.
    /// </summary>
    public Task<T> RunAsync<T>(Func<CancellationToken, T> operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            deadline.CancelAfter(timeout);
        }

        var registration = deadline.Token.Register(() =>
        {
            var kind = cancellationToken.IsCancellationRequested
                ? AutomationFailureKind.Cancelled
                : AutomationFailureKind.TimedOut;
            var message = kind == AutomationFailureKind.Cancelled
                ? "The operation was cancelled."
                : "The operation did not complete within the allotted timeout.";
            tcs.TrySetException(new AutomationFailure(kind, message));
        });

        _queue.Add(() =>
        {
            using var _1 = registration;
            using var _2 = deadline;

            // If the deadline already fired while this item was still queued, the
            // operation must never run.
            if (deadline.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var result = operation(deadline.Token);
                tcs.TrySetResult(result);
            }
            catch (AutomationFailure failure)
            {
                // The operation raised its own stable, typed failure (e.g. a missing
                // window) - pass it through unchanged.
                tcs.TrySetException(failure);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(new AutomationFailure(
                    AutomationFailureKind.ElementUnavailable,
                    $"The UI Automation operation failed: {ex.Message}",
                    ex));
            }
        });

        return tcs.Task;
    }

    private void RunLoop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                item();
            }
            catch
            {
                // Every failure path inside a queued item already reports itself onto
                // its own TaskCompletionSource; nothing here should ever be observable,
                // but the worker loop must never die from a single bad item.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
