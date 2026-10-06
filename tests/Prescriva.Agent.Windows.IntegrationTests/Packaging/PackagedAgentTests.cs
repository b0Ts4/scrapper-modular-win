using System.Diagnostics;
using System.Windows.Automation;
using Microsoft.Win32;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.Packaging;

/// <summary>
/// The MSIX package built by packaging/build-msix.ps1, installed on this machine: the Agent
/// starts from its package (with package identity) and "Iniciar com o Windows" turns the
/// package's startup task on and off - the state Windows itself keeps for the task, which is
/// what Task Manager shows and what starts the Agent at sign-in. (A Run-key write from inside
/// a package would be virtualized and could look as if it worked; this checks the real task.)
/// Runs only where CI installed the package: PRESCRIVA_PACKAGE_FAMILY_NAME names it.
/// </summary>
[Trait("Category", "Packaged")]
public sealed class PackagedAgentTests
{
    private const string StartupTaskId = "PrescrivaAgentStartup";
    private const int StartupTaskEnabled = 2;

    [Fact]
    public async Task The_installed_package_starts_and_turns_its_startup_task_on_and_off()
    {
        var familyName = Environment.GetEnvironmentVariable("PRESCRIVA_PACKAGE_FAMILY_NAME");
        Assert.False(string.IsNullOrWhiteSpace(familyName), "Install the MSIX and set PRESCRIVA_PACKAGE_FAMILY_NAME to run this test.");

        Assert.NotEqual(StartupTaskEnabled, StartupTaskState(familyName!));

        using (var agent = await LaunchPackagedAsync(familyName!))
        {
            Assert.Contains(@"\WindowsApps\", agent.Process.MainModule!.FileName, StringComparison.OrdinalIgnoreCase);
            var option = (TogglePattern)Find(agent.Window, "StartWithWindowsCheckBox").GetCurrentPattern(TogglePattern.Pattern);
            Assert.Equal(ToggleState.Off, option.Current.ToggleState);

            option.Toggle();
            await WaitForTextAsync(agent.Window, "StatusText", "Iniciar com o Windows: ativado");
            Assert.Equal(StartupTaskEnabled, StartupTaskState(familyName!));
            Close(agent);
        }

        using (var reopened = await LaunchPackagedAsync(familyName!))
        {
            var option = (TogglePattern)Find(reopened.Window, "StartWithWindowsCheckBox").GetCurrentPattern(TogglePattern.Pattern);
            await WaitUntilAsync(() => option.Current.ToggleState == ToggleState.On, () => "the reopened Agent does not show the startup task as enabled");

            option.Toggle();
            await WaitForTextAsync(reopened.Window, "StatusText", "Iniciar com o Windows: desativado");
            Assert.NotEqual(StartupTaskEnabled, StartupTaskState(familyName!));
            Close(reopened);
        }
    }

    /// <summary>The state Windows keeps for the package's startup task (2 = enabled), or -1 when never set.</summary>
    private static int StartupTaskState(string familyName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\{familyName}\{StartupTaskId}");
        return key?.GetValue("State") is int state ? state : -1;
    }

    private static async Task<PackagedAgent> LaunchPackagedAsync(string familyName)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $@"shell:AppsFolder\{familyName}!App") { UseShellExecute = false })?.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var process in Process.GetProcessesByName("Prescriva.Agent.Desktop"))
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return new PackagedAgent(process, AutomationElement.FromHandle(process.MainWindowHandle));
                }

                process.Dispose();
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("The packaged Agent did not show its window.");
    }

    private static void Close(PackagedAgent agent)
    {
        ((WindowPattern)agent.Window.GetCurrentPattern(WindowPattern.Pattern)).Close();
        Assert.True(agent.Process.WaitForExit(15_000), "The packaged Agent did not exit when closed.");
    }

    private sealed record PackagedAgent(Process Process, AutomationElement Window) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            Process.Dispose();
        }
    }
}
