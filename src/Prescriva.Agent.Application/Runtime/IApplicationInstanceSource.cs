using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Application.Runtime;

/// <summary>
/// A single running instance of a configured <see cref="ApplicationDefinition"/>. Plain
/// data: <see cref="InstanceId"/> is allocated by the source the moment it first observes
/// the instance and stays stable for that instance's whole lifetime; <see cref="ProcessId"/>
/// is the OS process ID, present for diagnostics and for Windows-layer factories that need
/// to scope a resolver/capture provider/trigger provider to this specific process.
/// </summary>
public sealed record ApplicationInstance(Guid InstanceId, int ProcessId);

public enum ApplicationInstanceChangeKind
{
    Started,
    Stopped
}

public sealed record ApplicationInstanceChange(ApplicationInstance Instance, ApplicationInstanceChangeKind Kind);

/// <summary>
/// The application-facing abstraction over discovering and tracking running instances of a
/// configured application. Implementations (in Prescriva.Agent.Windows) own all native
/// process/window enumeration; this interface and everything it yields is plain data.
///
/// Two separate running instances of the same configured application (e.g. two copies of
/// the same EHR window open side by side) must be reported as two distinct
/// <see cref="ApplicationInstance"/>s with distinct <see cref="ApplicationInstance.InstanceId"/>
/// values - this is what lets <see cref="AgentRuntime"/> keep their capture sessions
/// completely independent.
/// </summary>
public interface IApplicationInstanceSource
{
    /// <summary>
    /// Watches for instances of <paramref name="application"/> starting and stopping,
    /// yielding one <see cref="ApplicationInstanceChange"/> per observed transition.
    /// Cancelling <paramref name="cancellationToken"/> stops watching cleanly; it does not
    /// throw a failure through the enumerable in that case (same convention as
    /// <see cref="Triggers.ITriggerProvider.WatchAsync"/>).
    /// </summary>
    IAsyncEnumerable<ApplicationInstanceChange> WatchAsync(ApplicationDefinition application, CancellationToken cancellationToken);
}
