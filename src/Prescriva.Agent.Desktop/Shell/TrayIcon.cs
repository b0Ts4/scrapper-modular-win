using System.Drawing;
using System.Windows.Forms;

namespace Prescriva.Agent.Desktop.Shell;

/// <summary>
/// The Agent's visible presence in the notification area while its window is hidden: the
/// tooltip always says whether it is monitoring, and the menu shows the window, stops
/// monitoring or exits. Never hidden while the Agent runs.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action show, Action stopMonitoring, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mostrar Prescriva Agent", null, (_, _) => show());
        menu.Items.Add("Parar monitoramento", null, (_, _) => stopMonitoring());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Prescriva Agent",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    /// <summary>The hover text; Windows limits it to 127 characters.</summary>
    public void SetStatus(string text) =>
        _icon.Text = text.Length <= 127 ? text : text[..124] + "...";

    public void Notify(string title, string text, bool warning = false) =>
        _icon.ShowBalloonTip(5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
