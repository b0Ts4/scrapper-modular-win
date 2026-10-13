namespace Prescriva.Agent.Application.Runtime;

/// <summary>How the Agent was started.</summary>
public static class StartupLaunch
{
    public const string BackgroundArgument = "--background";

    /// <summary>
    /// True when Windows started the Agent at sign-in - through the Run key command
    /// (<see cref="BackgroundArgument"/>) or the packaged startup task - so it starts in the
    /// tray and resumes the integration left active; false when a person opened it.
    /// </summary>
    public static bool IsBackground(IReadOnlyList<string> arguments, bool activatedByStartupTask)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return activatedByStartupTask ||
            arguments.Any(argument => string.Equals(argument, BackgroundArgument, StringComparison.OrdinalIgnoreCase));
    }
}
