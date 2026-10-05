using System.Windows;
using Prescriva.Agent.Desktop.Shell;

namespace Prescriva.Agent.Desktop;

/// <summary>
/// Starts the Agent. <c>--background</c> (the command registered by "Iniciar com o Windows")
/// starts in the notification area and resumes the integration the operator left active.
/// Only one Agent runs per data directory: a second start asks the running one to show its
/// window and exits, so the same application is never monitored twice (duplicate events).
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var background = e.Args.Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        var dataDirectory = Desktop.MainWindow.DataDirectory;

        Desktop.MainWindow? window = null;
        _instance = SingleInstance.TryAcquire(dataDirectory, () => Dispatcher.BeginInvoke(() => window?.ShowFromTray()));
        if (_instance is null)
        {
            SingleInstance.SignalShow(dataDirectory);
            Shutdown(0);
            return;
        }

        window = new Desktop.MainWindow();
        MainWindow = window;
        if (background)
        {
            await window.StartInBackgroundAsync();
        }
        else
        {
            window.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
