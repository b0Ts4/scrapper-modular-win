using System.IO;
using System.Windows.Automation;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Desktop.Configuration;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Infrastructure.Configuration;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests;

/// <summary>
/// Task 5's Step 3 (manual verification) attempted first as a real, automated end-to-end
/// test, per the task brief: drive the actual production pieces - a real, launched
/// Prescriva.Agent.TestTarget process, the real <see cref="UiAutomationElementInspector"/>
/// / <see cref="InspectionController"/> pointer-inspection pipeline, the real
/// <see cref="UiAutomationSelectorResolver"/> and <see cref="UiAutomationCaptureProvider"/>,
/// and the real <see cref="JsonConfigurationStore"/> writing to a real temp directory -
/// through the full configurator workflow that <see cref="IntegrationEditorViewModel"/>
/// orchestrates: create integration -> point at TestTarget's MedicationTextBox -> confirm
/// selection -> assign a semantic ID -> save -> reload the config from disk (a fresh view
/// model instance, simulating an app restart) -> resolve -> capture the value -> assert it
/// matches what was actually in the live control.
///
/// What this test does NOT do: drive Prescriva.Agent.Desktop.exe's own compiled WPF UI
/// (MainWindow) via simulated OS-level mouse input (e.g. SendInput) to click its buttons.
/// Automating a second layer of UI Automation against the Agent's own UI - on top of the
/// UI Automation this test already uses to drive TestTarget - was judged impractical
/// within reasonable effort for this task (a further, more fragile layer of screen-
/// coordinate-dependent automation, clicking WPF buttons in this exact window, for
/// marginal additional proof beyond what driving the real production components directly
/// already demonstrates). See docs/testing/windows-inspector-manual.md for the honest
/// account of what remains unverified as a result (visually confirming the highlight
/// overlay and MainWindow's own buttons/text update correctly) and what was verified here
/// instead.
/// </summary>
public sealed class ConfiguratorVerticalSliceTests
{
    [Fact]
    public async Task Full_workflow_create_inspect_confirm_save_reload_resolve_and_read_a_real_value()
    {
        const string knownValue = "Amoxicillin 500mg";

        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();

        var medicationBox = target.Window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "MedicationTextBox"));
        Assert.NotNull(medicationBox);

        // Give the control a known value up front (via the real ValuePattern, not
        // simulated keystrokes) so the read-value step at the end has something concrete
        // and unambiguous to assert against.
        var seedValuePattern = (ValuePattern)medicationBox!.GetCurrentPattern(ValuePattern.Pattern);
        seedValuePattern.SetValue(knownValue);

        var rect = WaitForLaidOutBoundingRectangle(medicationBox);
        var point = new ScreenPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        // --- Point at TestTarget's MedicationTextBox and confirm the selection, exactly
        // as InspectorViewModel/MainWindow would drive InspectionController from a real
        // pointer position. ---
        var inspector = new UiAutomationElementInspector(dispatcher);
        var inspectionController = new InspectionController(inspector);
        await inspectionController.StartAsync();

        InspectionState state = inspectionController.CurrentState;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            medicationBox.SetFocus();
            await inspectionController.ObservePointerAsync(point);
            state = inspectionController.CurrentState;
            if (state.Snapshot?.AutomationId == "MedicationTextBox")
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.Equal("MedicationTextBox", state.Snapshot?.AutomationId);

        var confirmed = await inspectionController.ConfirmAsync();
        Assert.NotNull(confirmed.Fingerprint);

        // --- Build the real production configurator pipeline: real UIA resolver/capture
        // provider, and a real JsonConfigurationStore writing to a throwaway temp
        // directory (not a fake, not a mock). ---
        var configurationDirectory = Path.Combine(Path.GetTempPath(), "prescriva-agent-vertical-slice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configurationDirectory);
        try
        {
            var store = new JsonConfigurationStore(configurationDirectory);
            var resolver = new UiAutomationSelectorResolver(dispatcher);
            var captureProvider = new UiAutomationCaptureProvider(dispatcher);

            var editor = new IntegrationEditorViewModel(store, resolver, captureProvider);
            editor.CreateIntegration(
                "vertical-slice-integration",
                "Vertical Slice Integration",
                new ApplicationDefinition("Prescriva.Agent.TestTarget.exe", target.Window.Current.Name));
            editor.AddStage("intake", "Intake");
            editor.AddField("medication_name", "intake", "Medication name", required: true, confirmed.Fingerprint!);
            editor.AddTrigger(
                "finish_trigger",
                "intake",
                confirmed.Fingerprint! with { AutomationId = "FinishButton" },
                "Invoke",
                [new FinishSessionAction()]);

            Assert.True(editor.HasUnsavedChanges);
            await editor.SaveAsync();
            Assert.False(editor.HasUnsavedChanges);

            // --- Simulate an app restart: a fresh view model, reading only from disk. ---
            var restarted = new IntegrationEditorViewModel(
                new JsonConfigurationStore(configurationDirectory),
                new UiAutomationSelectorResolver(dispatcher),
                new UiAutomationCaptureProvider(dispatcher));
            await restarted.ReloadAsync("vertical-slice-integration");

            var reloadedField = Assert.Single(restarted.Configuration!.Fields);
            Assert.Equal("medication_name", reloadedField.Id);
            Assert.Equal("MedicationTextBox", reloadedField.Selector.AutomationId);

            var resolution = await restarted.ResolveFieldAsync("medication_name");
            Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);

            var captureResult = await restarted.ReadFieldValueAsync("medication_name");

            Assert.Equal(CaptureOutcome.Captured, captureResult.Outcome);
            Assert.Equal(knownValue, captureResult.Value);
        }
        finally
        {
            await inspectionController.StopAsync();
            try
            {
                Directory.Delete(configurationDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; nothing more we can do if this fails.
            }
        }
    }

    private static System.Windows.Rect WaitForLaidOutBoundingRectangle(AutomationElement element)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var rect = element.Current.BoundingRectangle;
            if (rect.Width > 1 && rect.Height > 1)
            {
                return rect;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException("Timed out waiting for the element's bounding rectangle to lay out.");
    }
}
