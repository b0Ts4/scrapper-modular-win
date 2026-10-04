using System.Diagnostics;
using System.Runtime.CompilerServices;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Windows.Processes;

/// <summary>
/// Implements <see cref="IApplicationInstanceSource"/> by polling
/// <see cref="Process.GetProcessesByName"/> for processes matching
/// <see cref="ApplicationDefinition.ProcessIdentity"/> (the .exe name, no extension - the
/// same convention <see cref="Automation.AutomationWindowLocator.Find"/> already uses) and,
/// when <see cref="ApplicationDefinition.WindowRule"/> is non-empty, matching each such
/// process's main window title against it. Each distinct OS process ID that matches is
/// reported as its own <see cref="ApplicationInstance"/> with a stable, freshly allocated
/// <see cref="ApplicationInstance.InstanceId"/> - this is what lets two separately launched
/// copies of the same configured application (same process name, same window title) be
/// told apart and tracked as independent instances.
///
/// Uses only <see cref="System.Diagnostics.Process"/> - no UI Automation, so no
/// <see cref="Automation.AutomationDispatcher"/> involvement is needed here.
/// </summary>
public sealed class WindowsApplicationInstanceSource : IApplicationInstanceSource
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _pollInterval;

    public WindowsApplicationInstanceSource(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public async IAsyncEnumerable<ApplicationInstanceChange> WatchAsync(
        ApplicationDefinition application,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);

        var known = new Dictionary<int, Guid>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seenThisPass = new HashSet<int>();
            foreach (var process in Process.GetProcessesByName(ProcessIdentity.ToProcessName(application.ProcessIdentity)))
            {
                using (process)
                {
                    if (!MatchesWindowRule(process, application.WindowRule))
                    {
                        continue;
                    }

                    seenThisPass.Add(process.Id);
                    if (!known.ContainsKey(process.Id))
                    {
                        var instanceId = Guid.NewGuid();
                        known[process.Id] = instanceId;
                        yield return new ApplicationInstanceChange(
                            new ApplicationInstance(instanceId, process.Id),
                            ApplicationInstanceChangeKind.Started);
                    }
                }
            }

            foreach (var stoppedProcessId in known.Keys.Where(processId => !seenThisPass.Contains(processId)).ToArray())
            {
                var instanceId = known[stoppedProcessId];
                known.Remove(stoppedProcessId);
                yield return new ApplicationInstanceChange(
                    new ApplicationInstance(instanceId, stoppedProcessId),
                    ApplicationInstanceChangeKind.Stopped);
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    private static bool MatchesWindowRule(Process process, string windowRule)
    {
        if (string.IsNullOrWhiteSpace(windowRule))
        {
            return true;
        }

        try
        {
            return string.Equals(process.MainWindowTitle, windowRule, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            // The process exited between GetProcessesByName and this check.
            return false;
        }
    }
}
