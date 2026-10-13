using System.Collections.Concurrent;
using System.Diagnostics;
using Prescriva.Agent.Application.Inspection;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Owns a dedicated background STA thread and marshals all UI Automation work onto it.
/// `System.Windows.Automation` is not reliably usable from arbitrary thread-pool threads, so
/// every real `AutomationElement` access in this codebase must go through
/// <see cref="RunAsync{T}"/> rather than touching UIA directly.
///
/// Every operation is bounded by a timeout and a <see cref="CancellationToken"/>. If the
/// caller cancels (or the timeout elapses) before the dispatcher thread reaches an item,
/// that item's operation is never invoked - only work already running on the dispatcher
/// thread can continue past the deadline (UIA offers no reliable way to abort a
/// synchronous COM call in flight), but its result is discarded and the caller still
/// observes the failure promptly.
///
/// Recovery: a call into a hung application can block its thread for minutes, and every
/// later call would queue behind it. When the running call has exceeded the wedge
/// threshold, the next call abandons that thread (it ends by itself once the stuck call
/// returns), moves any queued work to a fresh STA thread and continues there
/// (<see cref="RecoveredCount"/>).
/// </summary>
public sealed class AutomationDispatcher : IDisposable
{
    private static readonly TimeSpan DefaultWedgeThreshold = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly TimeSpan _wedgeThreshold;
    private Worker _worker;
    private int _recovered;
    private volatile bool _disposed;

    /// <param name="wedgeThreshold">How long one call may run before the thread is considered wedged (default 30 s).</param>
    public AutomationDispatcher(TimeSpan? wedgeThreshold = null)
    {
        _wedgeThreshold = wedgeThreshold ?? DefaultWedgeThreshold;
        _worker = new Worker();
    }

    /// <summary>The managed thread ID of the current dispatcher thread. Exposed for testing thread affinity.</summary>
    public int ThreadId => Volatile.Read(ref _worker).ThreadId;

    /// <summary>How many times a wedged thread was abandoned for a fresh one.</summary>
    public int RecoveredCount => Volatile.Read(ref _recovered);

    /// <summary>
    /// Runs <paramref name="operation"/> on the dispatcher's STA thread and returns its
    /// result, or fails with a typed <see cref="ElementInspectionFailure"/> (TimedOut,
    /// Cancelled, or the operation's own failure).
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

        void FailForDeadline()
        {
            var kind = cancellationToken.IsCancellationRequested
                ? ElementInspectionFailureKind.Cancelled
                : ElementInspectionFailureKind.TimedOut;
            var message = kind == ElementInspectionFailureKind.Cancelled
                ? "The operation was cancelled."
                : "The operation did not complete within the allotted timeout.";
            tcs.TrySetException(new ElementInspectionFailure(kind, message));
        }

        var registration = deadline.Token.Register(FailForDeadline);

        Enqueue(() =>
        {
            using var _1 = registration;
            using var _2 = deadline;

            // Complete the task here rather than relying on the registration's callback:
            // the token reports cancellation before its callbacks run, and disposing the
            // registration on the way out could remove the callback before it ever ran,
            // leaving the caller awaiting forever.
            if (deadline.IsCancellationRequested)
            {
                FailForDeadline();
                return;
            }

            try
            {
                var result = operation(deadline.Token);
                tcs.TrySetResult(result);
            }
            catch (ElementInspectionFailure failure)
            {
                // The operation raised its own stable, typed failure (e.g. a missing
                // window) - pass it through unchanged.
                tcs.TrySetException(failure);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(new ElementInspectionFailure(
                    ElementInspectionFailureKind.ElementUnavailable,
                    $"The UI Automation operation failed: {ex.Message}",
                    ex));
            }
        });

        return tcs.Task;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _worker.Complete(wait: TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Queues on the current worker, first replacing it when its running call is wedged.</summary>
    private void Enqueue(Action item)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker.RunningFor > _wedgeThreshold)
            {
                var wedged = _worker;
                _worker = new Worker();
                foreach (var pending in wedged.Abandon())
                {
                    _worker.Add(pending);
                }

                Interlocked.Increment(ref _recovered);
            }

            _worker.Add(item);
        }
    }

    private sealed class Worker
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private long _runningSince; // Stopwatch timestamp of the running item; 0 when idle

        public Worker()
        {
            _thread = new Thread(RunLoop)
            {
                IsBackground = true,
                Name = "Prescriva.Agent.Automation",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public TimeSpan RunningFor
        {
            get
            {
                var since = Interlocked.Read(ref _runningSince);
                return since == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(since);
            }
        }

        public void Add(Action item) => _queue.Add(item);

        /// <summary>Stops taking work and hands back what was still queued.</summary>
        public List<Action> Abandon()
        {
            _queue.CompleteAdding();
            var pending = new List<Action>();
            while (_queue.TryTake(out var item))
            {
                pending.Add(item);
            }

            return pending;
        }

        public void Complete(TimeSpan wait)
        {
            _queue.CompleteAdding();

            // A thread still inside a wedged UI Automation call keeps reading the queue when
            // the call returns: dispose it only once the thread has finished, never under it.
            if (_thread.Join(wait))
            {
                _queue.Dispose();
            }
        }

        private void RunLoop()
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                Interlocked.Exchange(ref _runningSince, Stopwatch.GetTimestamp());
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
                finally
                {
                    Interlocked.Exchange(ref _runningSince, 0);
                }
            }
        }
    }
}
