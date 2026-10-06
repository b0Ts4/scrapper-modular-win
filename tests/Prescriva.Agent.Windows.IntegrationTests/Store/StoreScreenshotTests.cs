using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;
using static Prescriva.Agent.Windows.IntegrationTests.EndToEnd.DesktopDriver;

namespace Prescriva.Agent.Windows.IntegrationTests.Store;

/// <summary>
/// Not a behavior test: drives the real Agent next to the TestTarget through the main flow
/// and saves full-screen screenshots for the Microsoft Store listing (at least 1366x768):
/// selecting a field, the test-mode results, and monitoring with captured events.
/// Run by CI after raising the screen resolution; PRESCRIVA_SCREENSHOT_DIR receives the PNGs.
/// </summary>
[Trait("Category", "StoreScreenshots")]
public sealed class StoreScreenshotTests : IDisposable
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "prescriva-store-screenshots-" + Guid.NewGuid().ToString("N"));

    public StoreScreenshotTests()
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
    public async Task Capture_the_Store_listing_screenshots()
    {
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PRESCRIVA_SCREENSHOT_DIR")), "Set PRESCRIVA_SCREENSHOT_DIR.");
        var (screenWidth, screenHeight) = (GetSystemMetrics(0), GetSystemMetrics(1));
        Assert.True(screenWidth >= 1366 && screenHeight >= 768, $"The Store needs at least 1366x768; the screen is {screenWidth}x{screenHeight}.");

        // A plain backdrop in the brand's light colour hides the CI console and desktop watermark.
        using var backdrop = Backdrop.Show();
        using var target = TestTargetLauncher.Launch();
        using var desktop = DesktopProcess.Launch(_dataDirectory);
        var agent = desktop.Window;
        Move(target.Window, 160, 70);
        Move(agent, 720, 30);
        ((TransformPattern)agent.GetCurrentPattern(TransformPattern.Pattern)).Resize(980, screenHeight - 110);

        // 1. Selecting fields on the pharmacy system with the mouse.
        await ConfigureAndSaveMedicineIntegrationAsync(agent, target);
        SetMedicine(target, "Dipirona", "500 mg", "2");
        Press(agent, "StartInspectionButton");
        await HoverAndConfirmAsync(agent, target, "MedicationTextBox");
        await Task.Delay(500);
        SaveScreenshot("store-1-selecionar-campos");
        Press(agent, "StopInspectionButton");

        // 2. Test mode: values read, signals, confidence, approval.
        await RunTestModeAsync(agent, target);
        ScrollUntilVisible(agent, "ApproveButton");
        await Task.Delay(500);
        SaveScreenshot("store-2-modo-de-teste");

        // 3. Monitoring: items and the finished budget captured automatically.
        Press(agent, "ApproveButton");
        await WaitForTextAsync(agent, "ApprovalStateText", "Aprovada em");
        Press(agent, "ActivateButton");
        await WaitUntilAsync(
            () => ListTexts(agent, "DiagnosticsList").Count(text => text.Contains("Monitorando gatilho", StringComparison.Ordinal)) >= 2,
            () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")));
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, _dataDirectory, 1);
        await Task.Delay(800);
        SetMedicine(target, "Amoxicilina", "875 mg", "1");
        Press(target.Window, "AddButton");
        await WaitForEventsAsync(agent, _dataDirectory, 2);
        ScrollUntilVisible(agent, "EventsList");
        await Task.Delay(500);
        SaveScreenshot("store-3-monitorando");

        Press(agent, "StopMonitoringButton");
        await WaitForTextAsync(agent, "MonitorStatusText", "Monitoramento parado");
        desktop.Close();
    }

    /// <summary>Scrolls the Agent's main view until the element is well inside the window.</summary>
    private static void ScrollUntilVisible(AutomationElement agent, string automationId)
    {
        var scroller = agent.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
        if (scroller is null)
        {
            return;
        }

        var scroll = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
        var window = agent.Current.BoundingRectangle;
        for (var percent = 0.0; percent <= 100; percent += 5)
        {
            scroll.SetScrollPercent(ScrollPattern.NoScroll, percent);
            var bounds = Find(agent, automationId).Current.BoundingRectangle;
            if (!bounds.IsEmpty && bounds.Bottom < window.Bottom - 20 && bounds.Top > window.Top + window.Height / 3)
            {
                return;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>A borderless full-screen window behind everything else, on its own STA thread.</summary>
    private sealed class Backdrop : IDisposable
    {
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly Thread _thread;

        private Backdrop(System.Windows.Threading.Dispatcher dispatcher, Thread thread)
        {
            _dispatcher = dispatcher;
            _thread = thread;
        }

        public static Backdrop Show()
        {
            System.Windows.Threading.Dispatcher? dispatcher = null;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                var window = new System.Windows.Window
                {
                    WindowStyle = System.Windows.WindowStyle.None,
                    ResizeMode = System.Windows.ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Left = 0,
                    Top = 0,
                    Width = System.Windows.SystemParameters.PrimaryScreenWidth,
                    Height = System.Windows.SystemParameters.PrimaryScreenHeight,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xF3, 0xF1)),
                    Title = "Store screenshot backdrop",
                };
                window.Show();
                dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                ready.Set();
                System.Windows.Threading.Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(10));
            return new Backdrop(dispatcher!, thread);
        }

        public void Dispose()
        {
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
