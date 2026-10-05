using Microsoft.Win32;
using Prescriva.Agent.Application.Runtime;

namespace Prescriva.Agent.Windows.Startup;

/// <summary>
/// Start with Windows through the current user's <c>Run</c> key: per user, no administrator
/// rights, and the Agent runs in the user's interactive session - which UI Automation and
/// DPAPI both require (a Windows service in session 0 could see neither the ERP nor the
/// user's protected data). The command is <c>"&lt;exe&gt;" --background</c>.
/// </summary>
public sealed class RunKeyStartupRegistration : IStartupRegistration
{
    public const string DefaultSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "PrescrivaAgent";
    public const string BackgroundArgument = "--background";

    private readonly string _subKey;

    /// <param name="subKey">HKCU subkey holding the value; tests pass an isolated one.</param>
    public RunKeyStartupRegistration(string subKey = DefaultSubKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subKey);
        _subKey = subKey;
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKey);
            return key?.GetValue(ValueName) is string { Length: > 0 };
        }
    }

    public void Enable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue(ValueName, $"\"{executablePath}\" {BackgroundArgument}", RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
