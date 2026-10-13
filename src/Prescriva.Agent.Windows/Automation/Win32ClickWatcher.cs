using System.Runtime.InteropServices;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Detects mouse clicks on one Win32 button that raises no UI Automation Invoke event (a
/// Delphi vendor button such as FórmulaCerta's TFagronButton): pressing it makes the button
/// capture the mouse, releasing it ends the capture. A click is a capture start and end on
/// that button's window with the pointer still over it at release. Listens, out of context,
/// only to the two capture events of the button's own process and looks only at the
/// configured button - no keyboard, no other window, no content. Activating the button with
/// the keyboard is not detected.
/// </summary>
internal sealed class Win32ClickWatcher : IDisposable
{
    private const uint EventSystemCaptureStart = 0x0008;
    private const uint EventSystemCaptureEnd = 0x0009;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WmQuit = 0x0012;

    private readonly IntPtr _button;
    private readonly Action _onClick;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly WinEventProcedure _procedure;
    private uint _threadId;
    private bool _pressed;

    public Win32ClickWatcher(IntPtr button, Action onClick)
    {
        _button = button;
        _onClick = onClick;
        _procedure = OnWinEvent; // kept in a field: native code calls it while the hook lives
        _thread = new Thread(Run) { IsBackground = true, Name = "Prescriva.Agent.Win32ClickWatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(5)) || Failure is not null)
        {
            throw new InvalidOperationException(Failure ?? "The click watcher did not start.");
        }
    }

    private string? Failure { get; set; }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(5));
        _started.Dispose();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        GetWindowThreadProcessId(_button, out var processId);
        var hook = processId == 0
            ? IntPtr.Zero
            : SetWinEventHook(EventSystemCaptureStart, EventSystemCaptureEnd, IntPtr.Zero, _procedure, processId, 0, WinEventOutOfContext);
        if (hook == IntPtr.Zero)
        {
            Failure = "Could not listen to the button's clicks (its window is gone).";
            _started.Set();
            return;
        }

        _started.Set();
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            UnhookWinEvent(hook);
        }
    }

    private void OnWinEvent(IntPtr hook, uint winEvent, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (window != _button)
        {
            return;
        }

        if (winEvent == EventSystemCaptureStart)
        {
            _pressed = true;
            return;
        }

        if (winEvent == EventSystemCaptureEnd && _pressed)
        {
            _pressed = false;
            if (GetCursorPos(out var point) && GetWindowRect(_button, out var rect) &&
                point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom)
            {
                _onClick();
            }
        }
    }

    private delegate void WinEventProcedure(IntPtr hook, uint winEvent, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProcedure procedure, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);
}
