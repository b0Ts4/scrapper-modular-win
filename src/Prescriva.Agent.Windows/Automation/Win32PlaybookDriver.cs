using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Windows.Processes;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Types into a Win32/Delphi program the way a person at its keyboard would (plan 19), without
/// moving the mouse or using the physical keyboard: controls are found by window path; focus
/// is given with the program's input attached (on a worker thread, because a Delphi program may
/// show a modal dialog from inside the focus change and not return until it is answered); text
/// is sent as <c>WM_CHAR</c>, keys as <c>WM_KEYDOWN/UP</c>, buttons pressed with <c>BM_CLICK</c>
/// (which FórmulaCerta's <c>TFagronButton</c> also answers). After each input the program's
/// queue is flushed with a <c>WM_NULL</c> so the next read sees the result. Before typing it
/// checks that the focus really is in the target (a click or focus change that did not land
/// must never send keys to another field). A dialog's text is read by OCR of its image, since
/// Delphi draws its labels. Nothing typed or read is logged.
/// </summary>
public sealed class Win32PlaybookDriver : IPlaybookDriver
{
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint WmNull = 0x0000;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmChar = 0x0102;
    private const uint EmSetSel = 0x00B1;
    private const uint BmClick = 0x00F5;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int VkReturn = 0x0D;
    private const int VkTab = 0x09;
    private static readonly TimeSpan KeySettle = TimeSpan.FromMilliseconds(150);

    private readonly TimeSpan _focusTimeout;
    private IntPtr _window;
    private HashSet<IntPtr> _screens = [];
    private uint _processId;

    public Win32PlaybookDriver(TimeSpan? focusTimeout = null)
    {
        _focusTimeout = focusTimeout ?? TimeSpan.FromSeconds(2);
    }

    public Task<bool> AttachAsync(string processIdentity, string windowRule, CancellationToken cancellationToken)
    {
        var processName = ProcessIdentity.ToProcessName(processIdentity);
        var processIds = Process.GetProcessesByName(processName).Select(process =>
        {
            using (process)
            {
                return (uint)process.Id;
            }
        }).ToHashSet();

        var candidates = TopLevelWindows(id => processIds.Contains(id))
            .Where(window => WindowRule.Matches(Text(window), windowRule))
            .ToArray();
        var chosen = candidates.FirstOrDefault(window => WindowRule.IsExact(Text(window), windowRule));
        if (chosen == IntPtr.Zero)
        {
            chosen = candidates.FirstOrDefault();
        }

        _window = chosen;
        _processId = 0;
        if (chosen != IntPtr.Zero)
        {
            GetWindowThreadProcessId(chosen, out _processId);
        }

        // The program's other screens already open (FórmulaCerta's Receitas next to Orçamentos)
        // are not dialogs: only windows that appear while the playbook runs are. When a dialog is
        // already open (Delphi's ShowModal disables every other window of the program), the
        // enabled window is that dialog and stays one.
        var mainEnabled = chosen != IntPtr.Zero && IsWindowEnabled(chosen);
        _screens = chosen == IntPtr.Zero
            ? []
            : TopLevelWindows(id => id == _processId).Where(window => window == chosen || mainEnabled || !IsWindowEnabled(window)).ToHashSet();
        return Task.FromResult(chosen != IntPtr.Zero);
    }

    public Task<WriteOutcome> SetTextAsync(string path, string text, CancellationToken cancellationToken) =>
        Task.FromResult(Write(path, control => SelectAll(control) && Chars(control, text) && Key(control, VkTab)));

    public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var control = Find(path);
        return Task.FromResult(control == IntPtr.Zero ? null : ReadText(control));
    }

    public Task<WriteOutcome> TypeAsync(string path, string text, CancellationToken cancellationToken) =>
        Task.FromResult(Write(path, control => Chars(control, text)));

    public Task<WriteOutcome> PressKeyAsync(string path, PlaybookKey key, CancellationToken cancellationToken) =>
        Task.FromResult(Write(path, control => Key(control, key == PlaybookKey.Enter ? VkReturn : VkTab)));

    public Task<WriteOutcome> PressAcceleratorAsync(string key, CancellationToken cancellationToken)
    {
        if (_window == IntPtr.Zero || string.IsNullOrEmpty(key))
        {
            return Task.FromResult(WriteOutcome.ControlNotFound);
        }

        var target = ForegroundFocus(_window);
        if (target == IntPtr.Zero)
        {
            return Task.FromResult(WriteOutcome.Blocked);
        }

        // Alt+<key>: the context-code bit (29) marks Alt held, as a real accelerator keystroke does.
        var vk = new IntPtr(char.ToUpperInvariant(key[0]));
        Post(target, WmSysKeyDown, vk, unchecked((IntPtr)0x20000001));
        Post(target, WmSysKeyUp, vk, unchecked((IntPtr)0xE0000001));
        Flush(target); // the command may open a dialog; the runner answers it next
        return Task.FromResult(WriteOutcome.Done);
    }

    public Task<string?> CaptionAsync(string path, CancellationToken cancellationToken) => ReadTextAsync(path, cancellationToken);

    public Task<WriteOutcome> ClickAsync(string path, CancellationToken cancellationToken)
    {
        var control = Find(path);
        if (control == IntPtr.Zero)
        {
            return Task.FromResult(WriteOutcome.ControlNotFound);
        }

        Post(control, BmClick, IntPtr.Zero, IntPtr.Zero);
        return Task.FromResult(Flush(control) ? WriteOutcome.Done : WriteOutcome.Blocked);
    }

    public Task<IReadOnlyList<OpenDialog>> DialogsAsync(IEnumerable<string> ignoredClasses, CancellationToken cancellationToken)
    {
        var ignored = ignoredClasses.ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<OpenDialog> dialogs = _window == IntPtr.Zero
            ? []
            : TopLevelWindows(id => id == _processId)
                .Where(window => !_screens.Contains(window) && !ignored.Contains(ClassOf(window)))
                .Select(window => new OpenDialog(window.ToInt64(), ClassOf(window), Text(window), Buttons(window)))
                .ToArray();
        return Task.FromResult(dialogs);
    }

    public async Task<string?> DialogTextAsync(OpenDialog dialog, CancellationToken cancellationToken)
    {
        var handle = new IntPtr(dialog.Handle);
        if (!IsWindow(handle))
        {
            return null;
        }

        // Screen pixels, as the capture needs them: UI Automation reports physical coordinates,
        // while GetWindowRect is scaled for a process that is not DPI-aware.
        System.Windows.Rect bounds;
        try
        {
            bounds = System.Windows.Automation.AutomationElement.FromHandle(handle).Current.BoundingRectangle;
        }
        catch (Exception exception) when (exception is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        var png = FileFieldCapture.CapturePng(bounds);
        var reading = await OcrFieldCapture.RecognizeAsync(png, cancellationToken).ConfigureAwait(false);
        return reading.Text;
    }

    public async Task<bool> PressDialogButtonAsync(OpenDialog dialog, string caption, CancellationToken cancellationToken)
    {
        var handle = new IntPtr(dialog.Handle);
        var button = Descendants(handle).FirstOrDefault(child => IsWindowVisible(child) && Playbook.Plain(ReadText(child)) == Playbook.Plain(caption));
        if (button == IntPtr.Zero)
        {
            return false;
        }

        Post(button, BmClick, IntPtr.Zero, IntPtr.Zero);
        for (var waited = 0; waited < 20 && IsWindow(handle) && IsWindowVisible(handle); waited++)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public Task<string?> ReadDialogFieldAsync(OpenDialog dialog, string path, CancellationToken cancellationToken)
    {
        var control = Win32WindowPath.Find(new IntPtr(dialog.Handle), path);
        return Task.FromResult(control == IntPtr.Zero ? null : ReadText(control));
    }

    public Task<bool> SetDialogTextAsync(OpenDialog dialog, string path, string text, CancellationToken cancellationToken)
    {
        var control = Win32WindowPath.Find(new IntPtr(dialog.Handle), path);
        if (control == IntPtr.Zero || !FocusOn(control, new IntPtr(dialog.Handle)))
        {
            return Task.FromResult(false);
        }

        if (!SelectAll(control) || !Chars(control, text) || !Key(control, VkTab))
        {
            return Task.FromResult(false);
        }

        // The dialog may look the value up when the field is left (19 -> the employee's name).
        for (var waited = 0; waited < 15 && !PlaybookRunner.SameValue(text, ReadText(control)); waited++)
        {
            Thread.Sleep(100);
        }

        return Task.FromResult(PlaybookRunner.SameValue(text, ReadText(control)));
    }

    private WriteOutcome Write(string path, Func<IntPtr, bool> input)
    {
        var control = Find(path);
        if (control == IntPtr.Zero)
        {
            return WriteOutcome.ControlNotFound;
        }

        if (!FocusOn(control, _window))
        {
            return WriteOutcome.Blocked;
        }

        // Focus was given, so some of the input may have been taken: never "blocked" (retyped) from here.
        return input(control) && Flush(control) ? WriteOutcome.Done : WriteOutcome.Interrupted;
    }

    private IntPtr Find(string path) => _window == IntPtr.Zero ? IntPtr.Zero : Win32WindowPath.Find(_window, path);

    /// <summary>
    /// Gives the focus to <paramref name="control"/> and checks that it is there. The call runs on
    /// a worker thread: a program may show a modal dialog from inside the focus change (FórmulaCerta's
    /// "Funcionário Recepção"), which holds the call until the dialog is answered - then this
    /// returns false and the caller answers the dialog first.
    /// </summary>
    private bool FocusOn(IntPtr control, IntPtr topLevel)
    {
        var thread = GetWindowThreadProcessId(control, out _);
        var worker = new Thread(() =>
        {
            var me = GetCurrentThreadId();
            AttachThreadInput(me, thread, true);
            try
            {
                SetForegroundWindow(topLevel);
                SetFocus(control);
            }
            finally
            {
                AttachThreadInput(me, thread, false);
            }
        })
        { IsBackground = true, Name = "Prescriva.Agent.PlaybookFocus" };
        worker.Start();
        if (!worker.Join(_focusTimeout))
        {
            return false;
        }

        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) && (info.Focus == control || IsChild(control, info.Focus));
    }

    /// <summary>Brings the window to the foreground and returns its focused control (or the window itself).</summary>
    private IntPtr ForegroundFocus(IntPtr window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var worker = new Thread(() =>
        {
            var me = GetCurrentThreadId();
            AttachThreadInput(me, thread, true);
            try
            {
                SetForegroundWindow(window);
            }
            finally
            {
                AttachThreadInput(me, thread, false);
            }
        })
        { IsBackground = true, Name = "Prescriva.Agent.PlaybookAccelerator" };
        worker.Start();
        if (!worker.Join(_focusTimeout))
        {
            return IntPtr.Zero;
        }

        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) && info.Focus != IntPtr.Zero ? info.Focus : window;
    }

    /// <summary>Selects the control's whole text, so what is typed replaces it.</summary>
    private static bool SelectAll(IntPtr control) => Send(control, EmSetSel, IntPtr.Zero, new IntPtr(-1));

    /// <summary>
    /// Types each character, waiting for the program to take it (a sent <c>WM_CHAR</c>: a posted one
    /// would still be queued when the value is read back). False when the program did not take it
    /// in time - e.g. a dialog opened while typing.
    /// </summary>
    private static bool Chars(IntPtr control, string text) =>
        text.All(character => Send(control, WmChar, new IntPtr(character), new IntPtr(1)));

    /// <summary>
    /// A key goes through the program's own message loop (posted), where Delphi turns Tab and
    /// Enter into moving between fields and cells; then the program is given a moment to handle it.
    /// </summary>
    private static bool Key(IntPtr control, int virtualKey)
    {
        Post(control, WmKeyDown, new IntPtr(virtualKey), new IntPtr(1));
        Post(control, WmKeyUp, new IntPtr(virtualKey), unchecked((IntPtr)0xC0000001));
        Thread.Sleep(KeySettle);
        return Flush(control);
    }

    /// <summary>True once the program answers (it is not hung); false when it did not answer in time.</summary>
    private static bool Flush(IntPtr control) => Send(control, WmNull, IntPtr.Zero, IntPtr.Zero);

    private static bool Send(IntPtr control, uint message, IntPtr wParam, IntPtr lParam) =>
        SendMessageTimeout(control, message, wParam, lParam, SmtoAbortIfHung, 3000, out _) != IntPtr.Zero;

    private static void Post(IntPtr control, uint message, IntPtr wParam, IntPtr lParam) => PostMessage(control, message, wParam, lParam);

    private static string ReadText(IntPtr control)
    {
        if (SendMessageTimeout(control, WmGetTextLength, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 2000, out var length) == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder((int)length.ToInt64() + 1);
        SendMessageTimeoutText(control, WmGetText, new IntPtr(buffer.Capacity), buffer, SmtoAbortIfHung, 2000, out _);
        return buffer.ToString();
    }

    private static ImmutableArray<string> Buttons(IntPtr dialog) =>
        Descendants(dialog)
            // TButton, TFagronButton, BUTTON - and Delphi's TBitBtn (the print dialog's Ok/Cancelar).
            .Where(child => IsWindowVisible(child) && ClassOf(child) is var name &&
                (name.Contains("Button", StringComparison.OrdinalIgnoreCase) || name.EndsWith("BitBtn", StringComparison.OrdinalIgnoreCase)))
            .Select(child => ReadText(child).Replace("&", string.Empty, StringComparison.Ordinal).Trim())
            .Where(caption => caption.Length > 0)
            .ToImmutableArray();

    private static IEnumerable<IntPtr> Descendants(IntPtr parent) => Win32WindowPath.Descendants(parent).Select(descendant => descendant.Handle);

    private static List<IntPtr> TopLevelWindows(Func<uint, bool> ofProcess)
    {
        var windows = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (IsWindowVisible(window) && ofProcess(processId))
            {
                windows.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static string ClassOf(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string Text(IntPtr window) => Win32WindowPath.Text(window);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProcedure procedure, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutText(IntPtr window, uint message, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
