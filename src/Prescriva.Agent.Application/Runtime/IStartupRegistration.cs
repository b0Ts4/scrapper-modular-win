namespace Prescriva.Agent.Application.Runtime;

/// <summary>Whether the Agent starts, in background mode, when the current user signs in to Windows.</summary>
public enum StartupRegistrationState
{
    Disabled,

    Enabled,

    /// <summary>The user turned it off in Task Manager (Startup apps); only the user can turn it back on there.</summary>
    DisabledByUser,

    /// <summary>An administrator's policy forbids it.</summary>
    DisabledByPolicy,

    /// <summary>An administrator's policy forces it on.</summary>
    EnabledByPolicy,
}

/// <summary>
/// Start with Windows: through the per-user Run key when the Agent runs unpackaged, through
/// the package's startup task when installed from the Microsoft Store.
/// </summary>
public interface IStartupRegistration
{
    Task<StartupRegistrationState> GetStateAsync();

    /// <returns>The resulting state, which is not <see cref="StartupRegistrationState.Enabled"/> when the user or a policy prevents it.</returns>
    Task<StartupRegistrationState> EnableAsync(string executablePath);

    Task<StartupRegistrationState> DisableAsync();
}
