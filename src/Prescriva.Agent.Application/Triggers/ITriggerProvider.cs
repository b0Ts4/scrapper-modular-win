using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Triggers;

/// <summary>
/// The application-facing abstraction over live UI trigger observation. Implementations
/// (in Prescriva.Agent.Windows) own all native UI Automation event subscription; this
/// interface and everything it yields is plain data, so the rest of the application
/// never needs to reference System.Windows.Automation.
/// </summary>
public interface ITriggerProvider
{
    /// <summary>
    /// Watches for the native UI event configured on <paramref name="trigger"/> and
    /// yields one <see cref="TriggerSignal"/> per native occurrence observed.
    /// Cancelling <paramref name="cancellationToken"/> stops watching and unsubscribes
    /// any native event handler cleanly; it does not throw a failure through the
    /// enumerable in that case, following the same convention as the Inspector plan's
    /// resolvers (cancellation surfaces as <see cref="OperationCanceledException"/>, not
    /// as a typed failure).
    /// </summary>
    IAsyncEnumerable<TriggerSignal> WatchAsync(TriggerDefinition trigger, CancellationToken cancellationToken);
}
