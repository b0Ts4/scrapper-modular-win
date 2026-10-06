using Prescriva.Agent.Application.Runtime;

namespace Prescriva.Agent.Application.Tests.Runtime;

/// <summary>
/// The Agent starts in the background (tray only, resuming the integration left active)
/// when Windows started it: through the Run key (<c>--background</c>) or, installed from the
/// Store, through its startup task. Otherwise it opens its window.
/// </summary>
public sealed class StartupLaunchTests
{
    [Fact]
    public void The_Run_key_command_starts_in_the_background() =>
        Assert.True(StartupLaunch.IsBackground(["--background"], activatedByStartupTask: false));

    [Fact]
    public void The_packaged_startup_task_starts_in_the_background() =>
        Assert.True(StartupLaunch.IsBackground([], activatedByStartupTask: true));

    [Fact]
    public void A_person_opening_the_Agent_gets_its_window()
    {
        Assert.False(StartupLaunch.IsBackground([], activatedByStartupTask: false));
        Assert.False(StartupLaunch.IsBackground(["--something-else"], activatedByStartupTask: false));
    }

    [Fact]
    public void The_argument_is_case_insensitive() =>
        Assert.True(StartupLaunch.IsBackground(["--BACKGROUND"], activatedByStartupTask: false));
}
