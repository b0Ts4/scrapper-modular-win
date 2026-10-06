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
