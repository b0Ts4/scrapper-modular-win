using Prescriva.Agent.Application.Runtime;
using WinRtStartupTask = Windows.ApplicationModel.StartupTask;
using WinRtStartupTaskState = Windows.ApplicationModel.StartupTaskState;

namespace Prescriva.Agent.Windows.Startup;

/// <summary>
/// Start with Windows for the Microsoft Store package: the package's <c>desktop:StartupTask</c>
/// (the Run key is virtualized inside a package and would have no effect). Windows starts
/// the Agent at sign-in through that task, which the Agent recognises as a background start.
/// </summary>
public sealed class PackagedStartupRegistration : IStartupRegistration
{
    /// <summary>The <c>TaskId</c> declared in the package manifest.</summary>
    public const string TaskId = "PrescrivaAgentStartup";

    public async Task<StartupRegistrationState> GetStateAsync() =>
        Map((await GetTaskAsync().ConfigureAwait(false)).State);

    public async Task<StartupRegistrationState> EnableAsync(string executablePath)
    {
        var task = await GetTaskAsync().ConfigureAwait(false);
        return Map(await task.RequestEnableAsync().AsTask().ConfigureAwait(false));
    }

    public async Task<StartupRegistrationState> DisableAsync()
    {
        var task = await GetTaskAsync().ConfigureAwait(false);
        task.Disable();
        return Map(task.State);
    }

    private static async Task<WinRtStartupTask> GetTaskAsync() =>
        await WinRtStartupTask.GetAsync(TaskId).AsTask().ConfigureAwait(false);

    private static StartupRegistrationState Map(WinRtStartupTaskState state) => state switch
    {
        WinRtStartupTaskState.Enabled => StartupRegistrationState.Enabled,
        WinRtStartupTaskState.DisabledByUser => StartupRegistrationState.DisabledByUser,
        WinRtStartupTaskState.DisabledByPolicy => StartupRegistrationState.DisabledByPolicy,
        WinRtStartupTaskState.EnabledByPolicy => StartupRegistrationState.EnabledByPolicy,
        _ => StartupRegistrationState.Disabled,
    };
}
