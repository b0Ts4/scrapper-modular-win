using System.Runtime.InteropServices;

namespace Prescriva.Agent.TestTarget;

/// <summary>
/// A native Win32 window built the way Delphi (VCL) programs build theirs - e.g. the
/// FórmulaCerta pharmacy system: child windows in a panel, no labels as windows, and every
/// child given its own window handle as control ID (VCL does SetWindowLong(GWL_ID, Handle)),
/// so UI Automation reports an AutomationId that changes on every start.
/// Shown with <c>--layout-variant win32-form</c>.
/// </summary>
internal static class Win32Form
{
    public const string Title = "Prescriva Win32 Form";

    // Values a test reads back; the order of the edits is what a window path relies on.
    public static readonly string[] EditTexts = ["DIPIRONA 500 MG", "20 ML", "2"];

    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint WsBorder = 0x00800000;
    private const uint WsTabStop = 0x00010000;
    private const int WsExTopmost = 0x00000008;
    private const int GwlId = -12;

    private static readonly List<IntPtr> Windows = [];

    // Window classes of their own, like VCL's TForm/TPanel (a STATIC would be transparent to hit-testing).
    private const string FormClass = "TPrescrivaForm";
    private const string PanelClass = "TPrescrivaPanel";

    /// <summary>Creates the window on the calling (UI) thread, whose message loop pumps it.</summary>
    public static void Show()
    {
        Register(FormClass);
        Register(PanelClass);
        var form = Create(WsExTopmost, FormClass, Title, WsOverlappedWindow | WsVisible, 40, 40, 420, 260, IntPtr.Zero);
        var panel = Child(PanelClass, string.Empty, WsChild | WsVisible, 10, 10, 380, 190, form);
        for (var index = 0; index < EditTexts.Length; index++)
        {
            Child("EDIT", EditTexts[index], WsChild | WsVisible | WsBorder | WsTabStop, 10, 10 + index * 32, 220, 24, panel);
        }

        // A vendor button like FórmulaCerta's TFagronButton: its own window class, the mouse is
        // captured while pressed, and UI Automation raises no Invoke event for it.
        RegisterButtonClass();
        Child(ButtonClass, "Adicionar", WsChild | WsVisible | WsTabStop | WsBorder, 10, 110, 110, 30, panel);

        // A vendor date box (FórmulaCerta's TwwDBDateTimePicker): UI Automation names it by its value.
        Register(DateClass);
        Child(DateClass, DateText, WsChild | WsVisible | WsBorder, 240, 10, 110, 24, panel);
    }

    public const string DateText = "14/07/26";
    private const string DateClass = "TPrescrivaDate";

    private const string ButtonClass = "TPrescrivaButton";
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;

    // Kept in a static field: the native class calls it for as long as the process runs.
    private static readonly WindowProcedure ButtonProcedure = ButtonWindowProcedure;

    private static IntPtr ButtonWindowProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmLButtonDown)
        {
            SetCapture(handle);
        }
        else if (message == WmLButtonUp)
        {
            ReleaseCapture();
        }

        return DefWindowProc(handle, message, wParam, lParam);
    }

    private static void RegisterButtonClass()
    {
        var windowClass = new WndClass
        {
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(ButtonProcedure),
            Instance = GetModuleHandle(null),
            Cursor = LoadCursor(IntPtr.Zero, IdcArrow),
            Background = ColorBtnFace + 1,
            ClassName = ButtonClass,
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClass({ButtonClass}) failed: {Marshal.GetLastWin32Error()}.");
        }
    }

    private delegate IntPtr WindowProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    private static IntPtr Child(string className, string text, uint style, int x, int y, int width, int height, IntPtr parent)
    {
        var handle = Create(0, className, text, style, x, y, width, height, parent);
        SetWindowLongPtr(handle, GwlId, handle); // what VCL does for a child created without an ID
        return handle;
    }

    private static IntPtr Create(int exStyle, string className, string text, uint style, int x, int y, int width, int height, IntPtr parent)
    {
        var handle = CreateWindowEx(exStyle, className, text, style, x, y, width, height, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx({className}) failed: {Marshal.GetLastWin32Error()}.");
        }

        Windows.Add(handle);
        return handle;
    }

    /// <summary>A class whose window procedure is DefWindowProc itself: no managed callback to keep alive.</summary>
    private static void Register(string className)
    {
        var windowClass = new WndClass
        {
            WindowProcedure = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            Instance = GetModuleHandle(null),
            Cursor = LoadCursor(IntPtr.Zero, IdcArrow),
            Background = ColorBtnFace + 1,
            ClassName = className,
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClass({className}) failed: {Marshal.GetLastWin32Error()}.");
        }
    }

    private const int IdcArrow = 32512;
    private const int ColorBtnFace = 15;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass windowClass);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int cursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
