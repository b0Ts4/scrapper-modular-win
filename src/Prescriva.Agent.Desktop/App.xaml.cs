using System.Windows;
using Prescriva.Agent.Desktop.Shell;

namespace Prescriva.Agent.Desktop;

/// <summary>
/// Starts the Agent. Started by Windows at sign-in (the Run key's <c>--background</c>, or the
/// Store package's startup task) it starts in the notification area and resumes the integration the operator left active.
/// Only one Agent runs per data directory: a second start asks the running one to show its
/// window and exits, so the same application is never monitored twice (duplicate events).
/// </summary>
public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var background = Prescriva.Agent.Application.Runtime.StartupLaunch.IsBackground(
            e.Args, Prescriva.Agent.Windows.Startup.PackageIdentity.ActivatedByStartupTask());
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
