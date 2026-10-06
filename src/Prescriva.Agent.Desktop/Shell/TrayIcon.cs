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
    /// <summary>The product name people see (Microsoft Store name).</summary>
    public const string ProductName = "Receita Fácil Agent";

    private readonly NotifyIcon _icon;

    public TrayIcon(Action show, Action stopMonitoring, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mostrar " + ProductName, null, (_, _) => show());
        menu.Items.Add("Parar monitoramento", null, (_, _) => stopMonitoring());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = BrandIcon(),
            Text = ProductName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    /// <summary>The executable's own (brand) icon; the generic application icon if it cannot be read.</summary>
    private static Icon BrandIcon()
    {
        try
        {
            return (Environment.ProcessPath is { } path ? Icon.ExtractAssociatedIcon(path) : null) ?? SystemIcons.Application;
        }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException)
        {
            return SystemIcons.Application;
        }
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
