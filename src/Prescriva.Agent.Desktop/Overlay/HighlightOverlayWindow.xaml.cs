using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Prescriva.Agent.Application.Inspection;

namespace Prescriva.Agent.Desktop.Overlay;

/// <summary>
/// A borderless, always-on-top window that draws a highlight rectangle around
/// <see cref="InspectionState.Bounds"/>. Must never intercept mouse input and must never
/// itself be treated as a candidate window by UI Automation consumers or Alt+Tab.
///
/// The mouse-transparency and Alt+Tab exclusion are OS-level concerns that WPF has no
/// managed API for, so they are applied via P/Invoke against the raw HWND once it
/// exists (in <see cref="OnSourceInitialized"/>):
///  - WS_EX_LAYERED + WS_EX_TRANSPARENT: click-through - mouse input passes to whatever
///    window is beneath this one instead of being captured by the overlay.
///  - WS_EX_NOACTIVATE: the overlay never steals focus/activation when shown.
///  - WS_EX_TOOLWINDOW: excludes the window from Alt+Tab and the taskbar.
///
/// This only affects mouse input routing. UI Automation hit-testing (AutomationElement.
/// FromPoint) is a separate mechanism and can still return this window's own element even
/// though it is click-through - excluding it from inspection results is the
/// responsibility of InspectionController (see its ProcessId-based exclusion), not this
/// class. See OverlayExclusionTests for the end-to-end proof.
/// </summary>
public partial class HighlightOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public HighlightOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// Positions the highlight border over <paramref name="bounds"/> and shows the
    /// window, or hides it (without closing it) when <paramref name="bounds"/> is null -
    /// mirroring InspectionState: no bounds means nothing should be highlighted.
    /// </summary>
    public void UpdateHighlight(BoundingRectangle? bounds)
    {
        if (bounds is not { } rect)
        {
            Hide();
            return;
        }

        Left = rect.X;
        Top = rect.Y;
        Width = Math.Max(rect.Width, 0);
        Height = Math.Max(rect.Height, 0);

        if (!IsVisible)
        {
            Show();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
