namespace Prescriva.Agent.Windows.Processes;

/// <summary>
/// Normalizes a configured process identity. Operators naturally type the executable file
/// name ("Erp.exe") while Windows process APIs (<see cref="System.Diagnostics.Process.GetProcessesByName(string)"/>,
/// <see cref="System.Diagnostics.Process.ProcessName"/>) only ever use the bare name
/// ("Erp"); without this, an identity with the extension silently matches nothing.
/// </summary>
public static class ProcessIdentity
{
    private const string ExecutableExtension = ".exe";

    /// <summary>The bare process name for <paramref name="configuredIdentity"/>: trimmed, without a trailing ".exe".</summary>
    public static string ToProcessName(string configuredIdentity)
    {
        ArgumentNullException.ThrowIfNull(configuredIdentity);

        var trimmed = configuredIdentity.Trim();
        return trimmed.EndsWith(ExecutableExtension, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^ExecutableExtension.Length]
            : trimmed;
    }

    /// <summary>True when a live process named <paramref name="processName"/> is the configured process.</summary>
    public static bool Matches(string configuredIdentity, string processName) =>
        string.Equals(ToProcessName(configuredIdentity), processName, StringComparison.OrdinalIgnoreCase);
}
