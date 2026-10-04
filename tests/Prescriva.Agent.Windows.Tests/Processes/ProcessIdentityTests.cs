using Prescriva.Agent.Windows.Processes;

namespace Prescriva.Agent.Windows.Tests.Processes;

/// <summary>
/// A configured process identity may be typed as the executable file name
/// ("App.exe") or as the bare process name ("App"); Windows APIs
/// (Process.GetProcessesByName, Process.ProcessName) only ever use the bare name, so both
/// spellings must name the same process instead of silently matching nothing.
/// </summary>
public sealed class ProcessIdentityTests
{
    [Theory]
    [InlineData("Prescriva.Agent.TestTarget.exe", "Prescriva.Agent.TestTarget")]
    [InlineData("Prescriva.Agent.TestTarget.EXE", "Prescriva.Agent.TestTarget")]
    [InlineData("  Prescriva.Agent.TestTarget.exe  ", "Prescriva.Agent.TestTarget")]
    [InlineData("Prescriva.Agent.TestTarget", "Prescriva.Agent.TestTarget")]
    [InlineData("erp", "erp")]
    public void ToProcessName_strips_an_executable_extension_and_surrounding_whitespace(string configured, string expected)
    {
        Assert.Equal(expected, ProcessIdentity.ToProcessName(configured));
    }

    [Theory]
    [InlineData("Prescriva.Agent.TestTarget.exe", "Prescriva.Agent.TestTarget", true)]
    [InlineData("prescriva.agent.testtarget", "Prescriva.Agent.TestTarget", true)]
    [InlineData("Prescriva.Agent.Desktop.exe", "Prescriva.Agent.TestTarget", false)]
    [InlineData("Prescriva.Agent.TestTarget.exe", "Prescriva.Agent.TestTarget.exe2", false)]
    public void Matches_compares_a_configured_identity_with_a_live_process_name(string configured, string processName, bool expected)
    {
        Assert.Equal(expected, ProcessIdentity.Matches(configured, processName));
    }
}
