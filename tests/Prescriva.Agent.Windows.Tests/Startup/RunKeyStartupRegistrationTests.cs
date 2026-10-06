using Microsoft.Win32;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Windows.Startup;

namespace Prescriva.Agent.Windows.Tests.Startup;

/// <summary>
/// "Iniciar com o Windows" outside a package writes one per-user value (no administrator
/// rights) whose command starts the Agent in background mode; disabling removes it.
/// Exercised against an isolated HKCU subkey, never the real Run key.
/// </summary>
public sealed class RunKeyStartupRegistrationTests : IDisposable
{
    private readonly string _subKey = @"Software\Prescriva\AgentTests\Run-" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);

    [Fact]
    public async Task Enabling_writes_a_quoted_background_command_and_disabling_removes_it()
    {
        var registration = new RunKeyStartupRegistration(_subKey);
        const string executable = @"C:\Program Files\Prescriva Agent\Prescriva.Agent.Desktop.exe";

        Assert.Equal(StartupRegistrationState.Disabled, await registration.GetStateAsync());
        Assert.Equal(StartupRegistrationState.Enabled, await registration.EnableAsync(executable));

        Assert.Equal(StartupRegistrationState.Enabled, await registration.GetStateAsync());
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey))
        {
            Assert.Equal($"\"{executable}\" --background", key!.GetValue(RunKeyStartupRegistration.ValueName));
        }

        Assert.Equal(StartupRegistrationState.Disabled, await registration.DisableAsync());
        using (var key = Registry.CurrentUser.OpenSubKey(_subKey))
        {
            Assert.Null(key!.GetValue(RunKeyStartupRegistration.ValueName));
        }
    }

    [Fact]
    public async Task Disabling_when_never_enabled_is_harmless_and_other_values_are_kept()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_subKey))
        {
            key.SetValue("OtherApp", "other.exe");
        }

        var registration = new RunKeyStartupRegistration(_subKey);
        await registration.DisableAsync();
        await registration.EnableAsync(@"C:\a.exe");
        await registration.DisableAsync();

        using var reopened = Registry.CurrentUser.OpenSubKey(_subKey);
        Assert.Equal("other.exe", reopened!.GetValue("OtherApp"));
    }

    [Fact]
    public void The_default_location_is_the_current_users_Run_key()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RunKeyStartupRegistration.DefaultSubKey);
    }
}
