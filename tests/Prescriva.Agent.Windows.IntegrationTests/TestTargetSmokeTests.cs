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
}
