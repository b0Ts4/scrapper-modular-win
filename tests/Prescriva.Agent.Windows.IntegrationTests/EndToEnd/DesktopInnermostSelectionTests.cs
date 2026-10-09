using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// A small value inside a larger element: pointing at TestTarget's price selects the product
/// row around it; holding Shift (real keyboard input) shows the price itself, and pressing Ctrl
/// with the mouse still over TestTarget confirms it - no trip back to the Agent's button.
/// </summary>
public sealed class DesktopInnermostSelectionTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-innermost-" + Guid.NewGuid().ToString("N"));

    public DesktopInnermostSelectionTests()
    {
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        SetKey(VkShift, down: false);
        SetKey(VkControl, down: false);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Holding_Shift_marks_the_field_inside_the_box_and_Ctrl_confirms_it()
    {
        using var target = TestTargetLauncher.Launch();
        using var desktop = DesktopProcess.Launch(_dataDirectory);
        var agent = desktop.Window;
        ArrangeSideBySide(agent, target);
        await ChooseProgramAsync(agent, "Test Target", $"PID {target.Window.Current.ProcessId}");
        SetText(agent, "IntegrationIdBox", "innermost");
        SetText(agent, "IntegrationNameBox", "Innermost");
        Press(agent, "CreateIntegrationButton");
        GoToStep(agent, "Step2Tab");
        Press(agent, "StartInspectionButton");

        var price = Automation.ElementInspectionTests.FindProductPrice(target).Current.BoundingRectangle;
        var (x, y) = ((int)(price.X + price.Width / 2), (int)(price.Y + price.Height / 2));

        // Without Shift the operator only reaches the product row around the price.
        await WaitUntilAsync(
            () => SetCursorPos(x, y) && Text(agent, "HoverStateText").Contains("AutomationId='ProductCard'", StringComparison.Ordinal),
            () => "hover shows: " + Text(agent, "HoverStateText"));

        // The operator is working in the other program (Ctrl typed in the Agent never confirms).
        target.Window.SetFocus();

        // Holding Shift: the price itself, said on screen.
        SetKey(VkShift, down: true);
        await WaitUntilAsync(
            () => SetCursorPos(x, y) && Text(agent, "HoverStateText").Contains("Nome='R$ 12,90'", StringComparison.Ordinal),
            () => "hover with Shift shows: " + Text(agent, "HoverStateText"));
        Assert.StartsWith("[Shift: elemento interno]", Text(agent, "HoverStateText"), StringComparison.Ordinal);
        Assert.True(OverlayMatches(price), "The outline must surround the price, not the card.");

        // Ctrl with the mouse still over TestTarget confirms it.
        SetKey(VkControl, down: true);
        SetKey(VkControl, down: false);
        SetKey(VkShift, down: false);
        await WaitForTextAsync(agent, "ConfirmedSelectionText", "Nome='R$ 12,90'");

        SetText(agent, "FieldMeaningBox", "Preço");
        Assert.Equal("preco", Value(agent, "FieldSemanticIdBox"));
        Press(agent, "AddFieldButton");
        await WaitForTextAsync(agent, "StatusText", "Campo 'preco' adicionado");
        Press(agent, "StopInspectionButton");
        desktop.Close();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
}
