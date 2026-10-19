using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Prescriva.Agent.Windows.Processes;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Detects a top-level window that appears in one program with a given window class and a
/// title starting with a given text (plan 17) - e.g. FórmulaCerta's "Mais Fórmula(s)?" question,
/// a Delphi TMessageForm titled "Orçamento &lt;n&gt;", shown once a formula is saved. Listens,
/// out of context, only to window-show events - of the program's process when known, otherwise
/// of every process with the program's name - and reads only the class and title of top-level
/// windows to compare them with the rule: no content, no input, nothing kept.
/// </summary>
internal sealed class Win32WindowShownWatcher : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WmQuit = 0x0012;
    private const uint GaRoot = 2;

    private readonly string _processName;
    private readonly int? _processId;
    private readonly string _className;
    private readonly string _titleStart;
    private readonly Action _onShown;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private readonly WinEventProcedure _procedure;
    private readonly Dictionary<uint, bool> _processMatches = [];
    private uint _threadId;

    public Win32WindowShownWatcher(string processIdentity, int? processId, string className, string titleStart, Action onShown)
    {
        _processName = ProcessIdentity.ToProcessName(processIdentity);
        _processId = processId;
        _className = className;
        _titleStart = titleStart;
        _onShown = onShown;
        _procedure = OnWinEvent; // kept in a field: native code calls it while the hook lives
        _thread = new Thread(Run) { IsBackground = true, Name = "Prescriva.Agent.Win32WindowShownWatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(5)) || Failure is not null)
        {
            throw new InvalidOperationException(Failure ?? "The window watcher did not start.");
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
        var hook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _procedure, (uint)(_processId ?? 0), 0, WinEventOutOfContext);
        if (hook == IntPtr.Zero)
        {
            Failure = "Could not listen to the program's windows.";
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
        if (objectId != ObjectIdWindow || window == IntPtr.Zero || GetAncestor(window, GaRoot) != window)
        {
            return;
        }

        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        if (!string.Equals(className.ToString(), _className, StringComparison.Ordinal))
        {
            return;
        }

        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        if (!title.ToString().StartsWith(_titleStart, StringComparison.OrdinalIgnoreCase) || !IsOfProgram(window))
        {
            return;
        }

        _onShown();
    }

    private bool IsOfProgram(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var processId);
        if (_processId is { } scoped)
        {
            return processId == scoped;
        }

        if (!_processMatches.TryGetValue(processId, out var matches))
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                matches = string.Equals(process.ProcessName, _processName, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                matches = false; // the process has already ended
            }

            _processMatches[processId] = matches;
        }

        return matches;
    }

    private delegate void WinEventProcedure(IntPtr hook, uint winEvent, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
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
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}
