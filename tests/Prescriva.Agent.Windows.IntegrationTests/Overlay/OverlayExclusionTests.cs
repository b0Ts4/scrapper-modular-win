using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Desktop.Overlay;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Overlay;

/// <summary>
/// Proves - against a real, visible HighlightOverlayWindow layered on top of a real
/// Prescriva.Agent.TestTarget window - that the overlay under the cursor is never
/// surfaced by InspectionController as the found element ("overlay sob o cursor deve
/// ser ignorado" from the milestone review focus).
///
/// Click-through (WS_EX_TRANSPARENT) affects mouse input routing, not UI Automation
/// visibility: AutomationElement.FromPoint can still return the overlay's own element
/// even though the overlay never receives mouse events. So this test does not assume
/// which of the two correct outcomes UI Automation will produce for a given Windows
/// version - it asserts the one thing that must never happen: the overlay window itself
/// being reported as the inspected element.
///
/// NOTE: on a machine with Windows Smart App Control enforced, running these tests can
/// fail with a FileLoadException loading Prescriva.Agent.Desktop.dll (CodeIntegrity event
/// IDs 3033/3077/3118). Release-configured usually fares better than Debug, but is not a
/// guaranteed fix - Smart App Control can still block a freshly built, unsigned Release
/// assembly identically. See docs/testing.md.
/// </summary>
public sealed class OverlayExclusionTests
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [Fact]
    public void HighlightOverlayWindow_sets_click_through_and_no_activate_extended_styles()
    {
        var host = OverlayHost.ShowOverTest(new BoundingRectangle(100, 100, 200, 80));
        try
        {
            var exStyle = GetWindowLong(host.Handle, GWL_EXSTYLE);

            Assert.True((exStyle & WS_EX_LAYERED) == WS_EX_LAYERED, "Expected WS_EX_LAYERED to be set.");
            Assert.True((exStyle & WS_EX_TRANSPARENT) == WS_EX_TRANSPARENT, "Expected WS_EX_TRANSPARENT (click-through) to be set.");
            Assert.True((exStyle & WS_EX_NOACTIVATE) == WS_EX_NOACTIVATE, "Expected WS_EX_NOACTIVATE to be set.");
            Assert.True((exStyle & WS_EX_TOOLWINDOW) == WS_EX_TOOLWINDOW, "Expected WS_EX_TOOLWINDOW (excluded from Alt+Tab) to be set.");
        }
        finally
        {
            host.Shutdown();
        }
    }

    [Fact]
    public async Task ObservePointerAsync_never_reports_the_overlay_itself_as_the_found_element()
    {
        using var target = TestTargetLauncher.Launch();
        using var dispatcher = new AutomationDispatcher();
        var inspector = new UiAutomationElementInspector(dispatcher);

        var medicationBox = target.Window.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "MedicationTextBox"));
        Assert.NotNull(medicationBox);

        var rect = WaitForLaidOutBoundingRectangle(medicationBox!);
        var bounds = new BoundingRectangle(rect.X, rect.Y, rect.Width, rect.Height);
        var point = new ScreenPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        var host = OverlayHost.ShowOverTest(bounds);
        try
        {
            // The overlay genuinely covers the target element's screen position - proven
            // directly, not assumed, before it can matter to the assertions below. The
            // Window instance has thread affinity to the STA thread OverlayHost created
            // it on, so every read of its properties must be marshaled there too.
            var (left, top, isVisible) = host.Invoke(w => (w.Left, w.Top, w.IsVisible));
            Assert.Equal(bounds.X, left, precision: 0);
            Assert.Equal(bounds.Y, top, precision: 0);
            Assert.True(isVisible);

            var controller = new InspectionController(inspector);
            await controller.StartAsync();

            InspectionState state = controller.CurrentState;
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await controller.ObservePointerAsync(point);
                state = controller.CurrentState;

                var resolvedRealElement = state.Snapshot is { AutomationId: "MedicationTextBox" } snapshot
                    && snapshot.ProcessId != Environment.ProcessId;
                var excludedTheOverlay = state.Snapshot is null
                    && state.Warnings.Any(w => w.Contains("own process", StringComparison.OrdinalIgnoreCase));

                if (resolvedRealElement || excludedTheOverlay)
                {
                    break;
                }

                await Task.Delay(200);
            }

            var overlayWasIncorrectlyReported =
                state.Snapshot is not null &&
                (state.Snapshot.ProcessId == Environment.ProcessId || state.Snapshot.AutomationId != "MedicationTextBox");

            Assert.False(
                overlayWasIncorrectlyReported,
                $"The overlay (or something other than the real underlying element) was reported as found: " +
                $"AutomationId='{state.Snapshot?.AutomationId}', ProcessId={state.Snapshot?.ProcessId} " +
                $"(this process is {Environment.ProcessId}).");

            var eitherExpectedOutcome =
                (state.Snapshot is { AutomationId: "MedicationTextBox" } s && s.ProcessId != Environment.ProcessId) ||
                (state.Snapshot is null && state.Warnings.Any(w => w.Contains("own process", StringComparison.OrdinalIgnoreCase)));

            Assert.True(
                eitherExpectedOutcome,
                $"Expected either the real underlying element (MedicationTextBox) to be resolved, or the " +
                $"overlay to be excluded with an 'own process' warning. Got Snapshot={state.Snapshot}, " +
                $"Warnings=[{string.Join(", ", state.Warnings)}].");
        }
        finally
        {
            host.Shutdown();
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    /// <summary>
    /// Hosts a real <see cref="HighlightOverlayWindow"/> on its own dedicated STA thread
    /// with a running <see cref="Dispatcher"/> message loop, so the window is a genuine,
    /// message-pumped HWND that both the OS and UI Automation see - not merely
    /// constructed and left unpumped.
    /// </summary>
    private sealed class OverlayHost
    {
        private readonly Thread _thread;
        private readonly Dispatcher _dispatcher;

        private OverlayHost(Thread thread, Dispatcher dispatcher, HighlightOverlayWindow window, IntPtr handle)
        {
            _thread = thread;
            _dispatcher = dispatcher;
            Window = window;
            Handle = handle;
        }

        public HighlightOverlayWindow Window { get; }

        public IntPtr Handle { get; }

        /// <summary>Runs <paramref name="func"/> on the overlay's own STA thread and returns its result.</summary>
        public T Invoke<T>(Func<HighlightOverlayWindow, T> func) => _dispatcher.Invoke(() => func(Window));

        public static OverlayHost ShowOverTest(BoundingRectangle bounds)
        {
            HighlightOverlayWindow? window = null;
            Dispatcher? dispatcher = null;
            IntPtr handle = IntPtr.Zero;
            var ready = new ManualResetEventSlim();

            var thread = new Thread(() =>
            {
                window = new HighlightOverlayWindow();
                window.UpdateHighlight(bounds);
                handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "Prescriva.Agent.OverlayTestHost",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Timed out waiting for the overlay window to be shown.");
            }

            return new OverlayHost(thread, dispatcher!, window!, handle);
        }

        public void Shutdown()
        {
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
