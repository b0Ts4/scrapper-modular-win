using System.IO;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Infrastructure.Configuration;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;
using Prescriva.Agent.Windows.Startup;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// "Iniciar com o Windows" through the real Desktop executable: the operator activates an
/// approved integration; the PC loses power (the process is killed); the Agent starts with
/// <c>--background</c> exactly as the Run key launches it - no window, no click - and captures
/// item_added. Opening the Agent again shows the running instance instead of starting a
/// second monitor (no duplicate events). An integration edited after its approval is never
/// resumed; the checkbox writes and removes the per-user Run value.
/// </summary>
public sealed class DesktopStartWithWindowsTests : IDisposable
{
    private const string ProcessName = "Prescriva.Agent.TestTarget";
    private const string WindowTitle = "Prescriva Agent Test Target";
    private const string ConfigurationId = "startup";

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-desktop-startup-" + Guid.NewGuid().ToString("N"));

    public DesktopStartWithWindowsTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Starting_with_Windows_resumes_the_approved_integration_left_active_without_any_click()
    {
        var configuration = BuildConfiguration();
        await SaveAsync(configuration, approvedFor: configuration);
        using var target = TestTargetLauncher.Launch();

        // The operator activates the approved integration, which is remembered as "left active".
        using (var operatorSession = DesktopProcess.Launch(_dataDirectory))
        {
            var agent = operatorSession.Window;
            SetText(agent, "IntegrationIdBox", ConfigurationId);
            Press(agent, "ReloadButton");
            await WaitForTextAsync(agent, "StatusText", $"Reloaded '{ConfigurationId}'");
            Press(agent, "ActivateButton");
            await WaitForMonitoringAsync(agent);
            await WaitUntilAsync(
                () => new JsonMonitoringPreferenceStore(_dataDirectory).GetActiveConfigurationIdAsync(CancellationToken.None).Result == ConfigurationId,
                () => "the activated integration was not remembered as left active");

            // Power loss: no orderly shutdown.
        }

        // Windows starts the Agent from the Run key: tray only, monitoring resumes on its own.
        using var background = DesktopProcess.LaunchBackground(_dataDirectory);
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(background.HasExited, "The background Agent exited.");
        Assert.False(background.HasVisibleWindow, "Starting with Windows must not open the window.");

        await PressAddUntilPersistedAsync(target, "Startup-1", expected: 1);
        Assert.False(background.HasVisibleWindow);

        // Opening the Agent again shows the running instance and exits - never a second monitor.
        using (var second = DesktopProcess.LaunchWithoutWaiting(_dataDirectory))
        {
            Assert.True(second.WaitForExit(TimeSpan.FromSeconds(20)), "The second start did not hand over and exit.");
            Assert.Equal(0, second.ExitCode);
        }

        var shown = background.Window;
        await WaitForTextAsync(shown, "StatusText", $"Iniciado com o Windows: monitorando '{ConfigurationId}'");
        Assert.Contains("Monitorando", Text(shown, "MonitorStatusText"), StringComparison.Ordinal);

        await Task.Delay(TimeSpan.FromMilliseconds(800));
        await PressAddUntilPersistedAsync(target, "Startup-2", expected: 2);
        await Task.Delay(TimeSpan.FromSeconds(2));
        var pending = await ReadPendingAsync();
        Assert.Equal(["Startup-1", "Startup-2"], pending.Select(e => e.Payload.Fields["medication"]));
        Assert.All(pending, e => Assert.Equal(ConfigurationFingerprint.Compute(configuration), e.ConfigurationRevision));

        // Stop clears "left active": the next start with Windows monitors nothing.
        Press(shown, "StopMonitoringButton");
        await WaitForTextAsync(shown, "MonitorStatusText", "Monitoramento parado");
        await WaitUntilAsync(
            () => new JsonMonitoringPreferenceStore(_dataDirectory).GetActiveConfigurationIdAsync(CancellationToken.None).Result is null,
            () => "Stop did not clear the integration left active");
        background.Close();
        Assert.True(background.WaitForExit(TimeSpan.FromSeconds(15)), "Closing a stopped Agent must exit it.");
    }

    [Fact]
    public async Task An_integration_edited_after_its_approval_is_not_resumed_at_startup()
    {
        var approved = BuildConfiguration();
        var edited = approved with { Name = "Startup (editada)" };
        await SaveAsync(edited, approvedFor: approved);
        await new JsonMonitoringPreferenceStore(_dataDirectory).SetActiveConfigurationIdAsync(ConfigurationId, CancellationToken.None);
        using var target = TestTargetLauncher.Launch();

        using var background = DesktopProcess.LaunchBackground(_dataDirectory);
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.False(background.HasExited);
        SetMedicine(target, "Nao-Capturar");
        Press(target.Window, "AddButton");

        using (var second = DesktopProcess.LaunchWithoutWaiting(_dataDirectory))
        {
            Assert.True(second.WaitForExit(TimeSpan.FromSeconds(20)));
        }

        var shown = background.Window;
        await WaitForTextAsync(shown, "StatusText", "alterada desde o último teste aprovado");
        Assert.DoesNotContain("Monitorando", Text(shown, "MonitorStatusText"), StringComparison.Ordinal);
        Assert.Empty(await ReadPendingAsync());
        background.Close();
    }

    [Fact]
    public async Task The_start_with_Windows_option_writes_and_removes_the_per_user_background_command()
    {
        var subKey = @"Software\Prescriva\AgentTests\Run-" + Guid.NewGuid().ToString("N");
        try
        {
            using var desktop = DesktopProcess.Launch(_dataDirectory, new Dictionary<string, string> { ["PRESCRIVA_AGENT_RUN_KEY"] = subKey });
            var option = Find(desktop.Window, "StartWithWindowsCheckBox");
            var toggle = (TogglePattern)option.GetCurrentPattern(TogglePattern.Pattern);
            Assert.Equal(ToggleState.Off, toggle.Current.ToggleState);

            toggle.Toggle();
            await WaitForTextAsync(desktop.Window, "StatusText", "Iniciar com o Windows: ativado");
            Assert.Equal($"\"{DesktopProcess.ExecutablePath}\" --background", ReadRunValue(subKey), ignoreCase: true);

            toggle.Toggle();
            await WaitForTextAsync(desktop.Window, "StatusText", "Iniciar com o Windows: desativado");
            Assert.Null(ReadRunValue(subKey));
            desktop.Close();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
    }

    private static string? ReadRunValue(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(RunKeyStartupRegistration.ValueName) as string;
    }

    private async Task PressAddUntilPersistedAsync(TestTargetLauncher target, string medication, int expected)
    {
        SetMedicine(target, medication);
        Press(target.Window, "AddButton");
        await WaitUntilAsync(
            () => ReadPendingAsync().Result.Count >= expected,
            () => $"expected {expected} persisted event(s) / log: " + ReadTechnicalLog(_dataDirectory));
    }

    private async Task<IReadOnlyList<Prescriva.Agent.Domain.Events.DomainEvent>> ReadPendingAsync()
    {
        var path = Path.Combine(_dataDirectory, "events.db");
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return await new SqliteEventOutbox(path, new DpapiPayloadProtector()).ReadPendingAsync(CancellationToken.None);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private async Task SaveAsync(IntegrationConfiguration configuration, IntegrationConfiguration approvedFor)
    {
        await new JsonConfigurationStore(Path.Combine(_dataDirectory, "configurations")).SaveAsync(configuration, CancellationToken.None);
        await new JsonApprovalStore(Path.Combine(_dataDirectory, "approvals")).SaveAsync(
            new ConfigurationApproval(approvedFor.Id, ConfigurationFingerprint.Compute(approvedFor), DateTimeOffset.UtcNow),
            CancellationToken.None);
    }

    private async Task WaitForMonitoringAsync(AutomationElement agent) =>
        await WaitUntilAsync(
            () => ListTexts(agent, "DiagnosticsList").Any(text => text.Contains("Monitorando gatilho 'add_item'", StringComparison.Ordinal)),
            () => Text(agent, "MonitorStatusText") + " / " + string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / log: " + ReadTechnicalLog(_dataDirectory));

    private static void SetMedicine(TestTargetLauncher target, string medication)
    {
        SetText(target.Window, "MedicationTextBox", medication);
        SetText(target.Window, "ConcentrationTextBox", "500 mg");
        SetText(target.Window, "QuantityTextBox", "1");
    }

    private static IntegrationConfiguration BuildConfiguration() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        ConfigurationId,
        "Startup",
        new ApplicationDefinition(ProcessName, WindowTitle),
        [new FieldDefinition("medication", "budget", "Medicamento", Required: true, Selector: Fingerprint("MedicationTextBox", "ControlType.Edit"))],
        [new StageDefinition("budget", "Orçamento")],
        [
            new TriggerDefinition(
                "add_item",
                "budget",
                Fingerprint("AddButton", "ControlType.Button"),
                "Invoke",
                [new CaptureFieldsAction(["medication"]), new EmitEventAction("item_added")]),
        ]);

    private static ElementFingerprint Fingerprint(string automationId, string controlType) =>
        new(ProcessName, WindowTitle, AutomationId: automationId, ControlType: controlType);
}
