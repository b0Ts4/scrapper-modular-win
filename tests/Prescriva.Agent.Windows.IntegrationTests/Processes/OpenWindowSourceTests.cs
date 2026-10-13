using Prescriva.Agent.Windows.Processes;

namespace Prescriva.Agent.Windows.IntegrationTests.Processes;

/// <summary>
/// The "choose an open program" list: real top-level windows with a title, their process and
/// PID; never the Agent's own windows; never invisible or untitled ones.
/// </summary>
public sealed class OpenWindowSourceTests
{
    [Fact]
    public async Task A_running_program_is_listed_with_its_title_process_and_pid()
    {
        using var target = TestTargetLauncher.Launch();
        var pid = target.Window.Current.ProcessId;

        var windows = await new Win32OpenWindowSource().ListAsync(CancellationToken.None);

        var entry = Assert.Single(windows, window => window.ProcessId == pid);
        Assert.Equal("Prescriva Agent Test Target", entry.WindowTitle);
        Assert.Equal("Prescriva.Agent.TestTarget", entry.ProcessName);
        Assert.False(string.IsNullOrWhiteSpace(entry.AppName));
    }

    [Fact]
    public async Task The_Agents_own_windows_and_untitled_windows_are_never_listed()
    {
        var ownWindow = await ShowOwnWindowAsync("Janela do proprio Agent");
        try
        {
            var windows = await new Win32OpenWindowSource().ListAsync(CancellationToken.None);

            Assert.DoesNotContain(windows, window => window.ProcessId == Environment.ProcessId);
            Assert.All(windows, window => Assert.False(string.IsNullOrWhiteSpace(window.WindowTitle)));
        }
        finally
        {
            ownWindow.Dispatcher.InvokeShutdown();
        }
    }

    [Fact]
    public async Task A_closed_program_disappears_from_a_refreshed_list()
    {
        int pid;
        using (var target = TestTargetLauncher.Launch())
        {
            pid = target.Window.Current.ProcessId;
            Assert.Contains(await new Win32OpenWindowSource().ListAsync(CancellationToken.None), window => window.ProcessId == pid);
        }

        await Task.Delay(500);
        Assert.DoesNotContain(await new Win32OpenWindowSource().ListAsync(CancellationToken.None), window => window.ProcessId == pid);
    }

    /// <summary>
    /// Delphi (VCL) programs - e.g. the FórmulaCerta pharmacy system - own every form by a
    /// zero-size application window ("TApplication") that is sometimes visible, sometimes not.
    /// The form is what the person sees and what holds the fields, so it is listed; the
    /// zero-size owner never is.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_form_owned_by_a_zero_size_application_window_is_listed_instead_of_its_owner(bool ownerVisible)
    {
        using var windows = await DelphiLikeWindows.ShowAsync("Aplicacao Delphi de teste", "Formulario Delphi de teste", ownerVisible);

        // This process's windows are excluded by default; list them for this test only.
        var listed = await new Win32OpenWindowSource(ownProcessId: -1).ListAsync(CancellationToken.None);

        Assert.Contains(listed, window => window.WindowTitle == "Formulario Delphi de teste" && window.ProcessId == Environment.ProcessId);
        Assert.DoesNotContain(listed, window => window.WindowTitle == "Aplicacao Delphi de teste");
    }

    /// <summary>A zero-size application window and a form it owns, on a message-pumped thread.</summary>
    private sealed class DelphiLikeWindows : IDisposable
    {
        private const uint WsPopup = 0x80000000;
        private const uint WsVisible = 0x10000000;
        private const uint WsOverlappedWindow = 0x00CF0000;

        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly IntPtr _owner;
        private readonly IntPtr _form;

        private DelphiLikeWindows(System.Windows.Threading.Dispatcher dispatcher, IntPtr owner, IntPtr form)
        {
            _dispatcher = dispatcher;
            _owner = owner;
            _form = form;
        }

        public static Task<DelphiLikeWindows> ShowAsync(string ownerTitle, string formTitle, bool ownerVisible)
        {
            var shown = new TaskCompletionSource<DelphiLikeWindows>();
            var thread = new Thread(() =>
            {
                var owner = CreateWindowEx(0, "STATIC", ownerTitle, WsPopup | (ownerVisible ? WsVisible : 0), 700, 400, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                var form = CreateWindowEx(0, "STATIC", formTitle, WsOverlappedWindow | WsVisible, 100, 100, 300, 200, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                shown.SetResult(new DelphiLikeWindows(System.Windows.Threading.Dispatcher.CurrentDispatcher, owner, form));
                System.Windows.Threading.Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return shown.Task;
        }

        public void Dispose()
        {
            _dispatcher.Invoke(() =>
            {
                DestroyWindow(_form);
                DestroyWindow(_owner);
            });
            _dispatcher.InvokeShutdown();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr handle);
    }

    private static Task<System.Windows.Window> ShowOwnWindowAsync(string title)
    {
        var shown = new TaskCompletionSource<System.Windows.Window>();
        var thread = new Thread(() =>
        {
            var window = new System.Windows.Window { Title = title, Width = 300, Height = 200, ShowActivated = false };
            window.Show();
            shown.SetResult(window);
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return shown.Task;
    }
}
