using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Prescriva.Agent.Desktop.Shell;

/// <summary>
/// Shown on top of every window while the robot types into the pharmacy system (design §3:
/// visible operation): what it is doing and a Parar button that stops it and leaves the screen
/// as it is. Not focusable, so it never takes the keyboard from the program the robot types into.
/// </summary>
internal sealed class RobotBanner : Window
{
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };

    public RobotBanner(Action stop)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = new SolidColorBrush(Color.FromRgb(0xB4, 0x3A, 0x0E));
        Title = "Robô trabalhando";
        var button = new Button { Content = "Parar", Margin = new Thickness(0, 6, 8, 6), Padding = new Thickness(14, 4, 14, 4), Focusable = false };
        button.Click += (_, _) => stop();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_text);
        panel.Children.Add(button);
        Content = panel;
        Loaded += (_, _) =>
        {
            Left = (SystemParameters.WorkArea.Width - ActualWidth) / 2;
            Top = SystemParameters.WorkArea.Top + 4;
        };
    }

    public void ShowWorking(string message)
    {
        _text.Text = message;
        if (!IsVisible)
        {
            Show();
        }
    }
}
