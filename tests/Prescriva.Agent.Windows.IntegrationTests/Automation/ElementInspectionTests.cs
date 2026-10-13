using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// Exercises UiAutomationElementInspector against a real, launched
/// Prescriva.Agent.TestTarget process, so these prove behavior against live UI
/// Automation rather than a mock or fake.
/// </summary>
public sealed class ElementInspectionTests
{
    [Fact]
    public async Task FromPointAsync_returns_a_Found_snapshot_for_a_real_element()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var inspector = new UiAutomationElementInspector(dispatcher);

        var medicationBox = target.Window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "MedicationTextBox"));
        Assert.NotNull(medicationBox);

        var rect = WaitForLaidOutBoundingRectangle(medicationBox!);
        var point = new ScreenPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        // This shared desktop can have other windows (from unrelated concurrent activity)
        // transiently land on top of the same screen coordinates. Bring the target
        // window forward and retry a few times rather than accepting a false failure
        // caused by something else briefly occluding the point.
        InspectionResult result = null!;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            medicationBox!.SetFocus();
            result = await inspector.FromPointAsync(point, TimeSpan.FromSeconds(10), CancellationToken.None);
            if (result.Outcome == InspectionOutcome.Found && result.Snapshot?.AutomationId == "MedicationTextBox")
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.Equal(InspectionOutcome.Found, result.Outcome);
        Assert.Equal("MedicationTextBox", result.Snapshot?.AutomationId);
    }

    [Theory]
    [InlineData("AddButton")]
    [InlineData("FinishButton")]
    public async Task FromPointAsync_over_a_buttons_caption_reports_the_button_not_its_inner_text(string automationId)
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var inspector = new UiAutomationElementInspector(dispatcher);

        var button = target.Window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        Assert.NotNull(button);

        // The centre of a WPF button is covered by its caption TextBlock, which UI
        // Automation hit-tests first. An operator pointing at the button means the button.
        var rect = WaitForLaidOutBoundingRectangle(button!);
        var point = new ScreenPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        InspectionResult result = null!;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            button!.SetFocus();
            result = await inspector.FromPointAsync(point, TimeSpan.FromSeconds(10), CancellationToken.None);
            if (result.Snapshot?.ProcessId == target.Window.Current.ProcessId)
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.Equal(InspectionOutcome.Found, result.Outcome);
        Assert.Equal(automationId, result.Snapshot?.AutomationId);
        Assert.Equal("ControlType.Button", result.Snapshot?.ControlType);
    }

    [Fact]
    public async Task A_value_inside_a_larger_element_is_reported_as_that_element_unless_the_innermost_is_asked_for()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var inspector = new UiAutomationElementInspector(dispatcher);
        var price = FindProductPrice(target);
        var rect = WaitForLaidOutBoundingRectangle(price);
        var point = new ScreenPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        var interactive = await InspectAsync(inspector, target, point, InspectionDepth.Interactive);
        var innermost = await InspectAsync(inspector, target, point, InspectionDepth.Innermost);

        Assert.Equal("ProductCard", interactive.Snapshot?.AutomationId); // what the operator could not get past
        Assert.Equal("ControlType.Text", innermost.Snapshot?.ControlType);
        Assert.Equal("R$ 12,90", innermost.Snapshot?.Name);
    }

    /// <summary>The price text inside TestTarget's product row (it has no AutomationId of its own).</summary>
    internal static AutomationElement FindProductPrice(TestTargetLauncher target) =>
        target.Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ProductCard"))
            ?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "R$ 12,90"))
        ?? throw new InvalidOperationException("The product price was not found.");

    [Fact]
    public async Task The_innermost_element_is_never_promoted_to_the_button_around_it()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var inspector = new UiAutomationElementInspector(dispatcher);
        var button = target.Window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "AddButton"));
        var rect = WaitForLaidOutBoundingRectangle(button!);
        var point = new ScreenPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        var innermost = await InspectAsync(inspector, target, point, InspectionDepth.Innermost);

        Assert.Equal(InspectionOutcome.Found, innermost.Outcome);
        Assert.Equal("ControlType.Text", innermost.Snapshot?.ControlType);
        Assert.Equal("Add", innermost.Snapshot?.Name);
    }

    private static async Task<InspectionResult> InspectAsync(UiAutomationElementInspector inspector, TestTargetLauncher target, ScreenPoint point, InspectionDepth depth)
    {
        InspectionResult result = null!;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            target.Window.SetFocus();
            result = await inspector.FromPointAsync(point, depth, TimeSpan.FromSeconds(10), CancellationToken.None);
            if (result.Snapshot?.ProcessId == target.Window.Current.ProcessId)
            {
                break;
            }

            await Task.Delay(200);
        }

        return result;
    }

    [Fact]
    public async Task FromPointAsync_returns_TimedOut_when_the_timeout_is_effectively_immediate()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var inspector = new UiAutomationElementInspector(dispatcher);

        var rect = target.Window.Current.BoundingRectangle;
        var point = new ScreenPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        var result = await inspector.FromPointAsync(point, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(InspectionOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task FindCandidatesAsync_returns_snapshots_for_the_real_target_windows_elements()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var inspector = new UiAutomationElementInspector(dispatcher);

        var processName = Process.GetProcessById(target.Window.Current.ProcessId).ProcessName;
        var application = new ApplicationDefinition(processName, target.Window.Current.Name);

        var candidates = await inspector.FindCandidatesAsync(application, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Contains(candidates, c => c.AutomationId == "MedicationTextBox");
        Assert.Contains(candidates, c => c.AutomationId == "NextButton");
    }

    [Fact]
    public async Task FindCandidatesAsync_reports_WindowMissing_once_the_target_process_has_exited()
    {
        string processName;
        string windowName;
        using (var target = TestTargetLauncher.Launch())
        {
            processName = Process.GetProcessById(target.Window.Current.ProcessId).ProcessName;
            windowName = target.Window.Current.Name;
        }

        // The `using` block above disposed the launcher, killing the process, so the
        // window this ApplicationDefinition points at no longer exists.
        using var dispatcher = new AutomationDispatcher();
        var inspector = new UiAutomationElementInspector(dispatcher);
        var application = new ApplicationDefinition(processName, windowName);

        var failure = await Assert.ThrowsAsync<ElementInspectionFailure>(
            () => inspector.FindCandidatesAsync(application, TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Equal(ElementInspectionFailureKind.WindowMissing, failure.Kind);
    }

    [Fact]
    public async Task FindCandidatesAsync_throws_OperationCanceledException_when_its_own_token_is_cancelled()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var inspector = new UiAutomationElementInspector(dispatcher);

        var processName = Process.GetProcessById(target.Window.Current.ProcessId).ProcessName;
        var application = new ApplicationDefinition(processName, target.Window.Current.Name);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A caller following the normal .NET cancellation convention catches
        // OperationCanceledException, not a generic typed failure - FindCandidatesAsync
        // must honor that the same way FromPointAsync does.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => inspector.FindCandidatesAsync(application, TimeSpan.FromSeconds(5), cts.Token));
    }

    [Fact]
    public void ElementSnapshot_never_exposes_a_native_UI_Automation_reference()
    {
        foreach (var property in typeof(ElementSnapshot).GetProperties())
        {
            Assert.False(
                typeof(AutomationElement).IsAssignableFrom(property.PropertyType),
                $"ElementSnapshot.{property.Name} must not expose a native AutomationElement.");
        }
    }

    /// <summary>
    /// Immediately after launch, WPF may report a found element's bounding rectangle
    /// before layout has fully settled (e.g. collapsed to the window's title bar area).
    /// Polls until the rectangle has a real size so point-based tests target the actual
    /// control rather than whatever is momentarily at (0,0).
    /// </summary>
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
