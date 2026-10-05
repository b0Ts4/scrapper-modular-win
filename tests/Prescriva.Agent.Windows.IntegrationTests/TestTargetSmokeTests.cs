using System.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests;

/// <summary>
/// Launches the real, compiled Prescriva.Agent.TestTarget.exe and inspects it with
/// live UI Automation, confirming every control the rest of the plan depends on is
/// present with a stable automation ID.
/// </summary>
public class TestTargetSmokeTests
{
    private static readonly string[] RequiredAutomationIds =
    [
        "MedicationTextBox",
        "ConcentrationTextBox",
        "QuantityTextBox",
        "FormComboBox",
        "ItemsGrid",
        "NextButton",
        "BackButton",
        "AddButton",
        "FinishButton",
        "CancelButton",
        "DynamicField",
        "ToggleDynamicFieldButton",
    ];

    [Fact]
    public void Launched_TestTarget_exposes_all_required_automation_ids()
    {
        using var target = TestTargetLauncher.Launch();

        foreach (var automationId in RequiredAutomationIds)
        {
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
            var element = target.Window.FindFirst(TreeScope.Descendants, condition);

            Assert.True(element is not null, $"Expected to find an element with AutomationId '{automationId}'.");
        }
    }

    /// <summary>
    /// Proves which build the suite actually exercised: 64-bit by default, 32-bit when CI
    /// sets PRESCRIVA_TESTTARGET_EXPECTED_BITNESS=32 for its x86 pass (so the x86 job
    /// cannot silently fall back to the x64 TestTarget).
    /// </summary>
    [Fact]
    public void Launched_TestTarget_runs_with_the_expected_bitness()
    {
        var expected = Environment.GetEnvironmentVariable("PRESCRIVA_TESTTARGET_EXPECTED_BITNESS") is { Length: > 0 } value ? value : "64";
        using var target = TestTargetLauncher.Launch();
        using var process = System.Diagnostics.Process.GetProcessById(target.Window.Current.ProcessId);

        Assert.True(IsWow64Process(process.Handle, out var isWow64));
        Assert.Equal(expected, isWow64 ? "32" : "64");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr process, out bool wow64Process);
}
