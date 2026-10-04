using System.Windows.Automation;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// Controls with a visible label but no AutomationId (TestTarget's "Observações:" and
/// "Lote:" fields): the inspector records their structural signals (label, ancestors,
/// relative position) and the resolver finds them again from those signals alone - after
/// a layout change too - while a duplicated label is reported ambiguous.
/// </summary>
public sealed class StructuralSelectorTests
{
    private const string NotesLabel = "Observações:";
    private const string BatchLabel = "Lote:";

    [Fact]
    public async Task Inspected_field_without_AutomationId_carries_its_label_and_relative_position_but_never_its_value()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        SetText(LabelledBox(target, NotesLabel), "valor-digitado-77");

        var fingerprint = await InspectAsync(target, dispatcher, NotesLabel);

        Assert.Null(fingerprint.AutomationId);
        Assert.Equal("ControlType.Edit", fingerprint.ControlType);
        Assert.Contains(NotesLabel, fingerprint.NearbyLabels);
        Assert.NotNull(fingerprint.RelativeBounds);
        Assert.DoesNotContain(fingerprint.NearbyLabels, label => label.Contains("valor-digitado-77", StringComparison.Ordinal));
        Assert.NotEqual("valor-digitado-77", fingerprint.Name);
    }

    [Fact]
    public async Task Field_without_AutomationId_is_found_and_read_by_its_label_in_both_layouts()
    {
        ElementFingerprint fingerprint;
        using (var target = TestTargetLauncher.Launch())
        using (var dispatcher = new AutomationDispatcher())
        {
            SetText(LabelledBox(target, NotesLabel), "notas-padrao");
            SetText(LabelledBox(target, BatchLabel), "lote-padrao");
            fingerprint = await InspectAsync(target, dispatcher, NotesLabel);
            Assert.Equal("notas-padrao", await ResolveAndReadAsync(target, dispatcher, fingerprint));
        }

        // Same labels, swapped positions: the label outweighs the stale position.
        using (var moved = TestTargetLauncher.Launch("alternate"))
        using (var dispatcher = new AutomationDispatcher())
        {
            SetText(LabelledBox(moved, NotesLabel), "notas-movidas");
            SetText(LabelledBox(moved, BatchLabel), "lote-movido");
            Assert.Equal("notas-movidas", await ResolveAndReadAsync(moved, dispatcher, fingerprint));
        }
    }

    [Fact]
    public async Task A_duplicated_label_makes_the_field_ambiguous_instead_of_guessing()
    {
        ElementFingerprint fingerprint;
        using (var target = TestTargetLauncher.Launch())
        using (var dispatcher = new AutomationDispatcher())
        {
            fingerprint = await InspectAsync(target, dispatcher, NotesLabel);
        }

        using var duplicated = TestTargetLauncher.Launch("duplicate-labels");
        using var resolverDispatcher = new AutomationDispatcher();
        using var resolver = new UiAutomationSelectorResolver(resolverDispatcher, processId: duplicated.Window.Current.ProcessId);

        var resolution = await resolver.ResolveAsync(fingerprint with { RelativeBounds = null }, CancellationToken.None);

        Assert.Equal(SelectorResolutionStatus.Ambiguous, resolution.Status);
    }

    private static async Task<ElementFingerprint> InspectAsync(TestTargetLauncher target, AutomationDispatcher dispatcher, string label)
    {
        var box = LabelledBox(target, label);
        var rect = box.Current.BoundingRectangle;
        var point = new ScreenPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var controller = new InspectionController(new UiAutomationElementInspector(dispatcher));
        await controller.StartAsync();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            box.SetFocus();
            await controller.ObservePointerAsync(point);
            if (controller.CurrentState.Snapshot?.ControlType == "ControlType.Edit" &&
                controller.CurrentState.Snapshot.ProcessId == target.Window.Current.ProcessId)
            {
                break;
            }

            await Task.Delay(200);
        }

        var confirmed = await controller.ConfirmAsync();
        return confirmed.Fingerprint!;
    }

    private static async Task<string?> ResolveAndReadAsync(TestTargetLauncher target, AutomationDispatcher dispatcher, ElementFingerprint fingerprint)
    {
        using var resolver = new UiAutomationSelectorResolver(dispatcher, processId: target.Window.Current.ProcessId);
        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);
        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);

        using var capture = new UiAutomationCaptureProvider(dispatcher);
        var result = await capture.CaptureAsync(
            resolution.Handle!,
            new FieldDefinition("notes", "budget", "Observações", Required: false, Selector: fingerprint),
            CancellationToken.None);
        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        return result.Value;
    }

    /// <summary>The text box that follows the given label (found by the label's text: the box has no AutomationId).</summary>
    internal static AutomationElement LabelledBox(TestTargetLauncher target, string label)
    {
        var labelElement = target.Window.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.NameProperty, label)))
            ?? throw new InvalidOperationException($"Label '{label}' not found.");
        return TreeWalker.ControlViewWalker.GetNextSibling(labelElement)
            ?? throw new InvalidOperationException($"No control follows label '{label}'.");
    }

    private static void SetText(AutomationElement element, string value) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
}
