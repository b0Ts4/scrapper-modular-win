using System.Runtime.InteropServices;
using System.Text;

namespace Prescriva.Agent.TestTarget;

/// <summary>
/// A quote screen that behaves like FórmulaCerta's Orçamentos where it matters to a playbook
/// (plan 19), all native Win32: two edits (volume, unit) and a components grid that takes typed
/// cells with Enter after each. Like FórmulaCerta, leaving the grid the first time asks
/// "Funcionário Recepção" in a modal dialog shown from inside the focus change; product 999
/// raises "Atenção!" / "Produto abaixo do Estoque Mínimo!"; product 666 raises an "Atenção!"
/// nobody has a rule for; and Enter on the empty row would save (a "Salvando" dialog), which a
/// playbook must never cause. There is also a decoy grid, so the components grid can only be
/// found by its window path. The grid shows its state as its window text, for tests only
/// (FórmulaCerta's grid has none). Shown with <c>--layout-variant win32-quote</c>.
/// </summary>
internal static class Win32Quote
{
    public const string Title = "Prescriva Orçamento";
    public const string FormClass = "TPrescrivaQuoteForm";
    public const string PanelClass = "TPrescrivaPanel";
    public const string GridClass = "TPrescrivaGrid";
    public const string EmployeeDialogClass = "TPrescrivaPedeFuncionario";
    public const string MessageClass = "TPrescrivaMessage";
    public const string SaveDialogClass = "TPrescrivaSalvando";

    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsPopup = 0x80000000;
    private const uint WsCaption = 0x00C00000;
    private const uint WsSysMenu = 0x00080000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint WsBorder = 0x00800000;
    private const uint WsTabStop = 0x00010000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExDlgModalFrame = 0x00000001;

    private const uint WmDestroy = 0x0002;
    private const uint WmKillFocus = 0x0008;
    private const uint WmCommand = 0x0111;
    private const uint WmKeyDown = 0x0100;
    private const uint WmChar = 0x0102;
    private const uint WmGetDlgCode = 0x0087;
    private const uint WmLButtonDown = 0x0201;
    private const int VkReturn = 0x0D;
    private const int DlgcWantAllKeys = 0x0004;
    private const int DlgcWantChars = 0x0080;

    // Kept in static fields: native code calls them for as long as the process runs.
    private static readonly WindowProcedure GridProcedure = GridWindowProcedure;
    private static readonly WindowProcedure DialogProcedure = DialogWindowProcedure;

    private static readonly List<string[]> Rows = [];
    private static readonly StringBuilder Cell = new();
    private static IntPtr _form;
    private static IntPtr _grid;
    private static int _column;
    private static bool _employeeAsked;
    private static string _employee = string.Empty;
    private static bool _saveAttempted;
    private static IntPtr _dialogEdit;

    public static void Show()
    {
        Register(FormClass, IntPtr.Zero);
        Register(PanelClass, IntPtr.Zero);
        Register(GridClass, Marshal.GetFunctionPointerForDelegate(GridProcedure));
        Register(EmployeeDialogClass, Marshal.GetFunctionPointerForDelegate(DialogProcedure));
        Register(MessageClass, Marshal.GetFunctionPointerForDelegate(DialogProcedure));
        Register(SaveDialogClass, Marshal.GetFunctionPointerForDelegate(DialogProcedure));

        _form = Create(0, FormClass, Title, WsOverlappedWindow | WsVisible, 40, 40, 520, 360, IntPtr.Zero);
        var fields = Create(0, PanelClass, string.Empty, WsChild | WsVisible, 10, 10, 490, 80, _form);
        Create(0, GridClass, string.Empty, WsChild | WsVisible | WsBorder, 300, 10, 180, 60, fields); // decoy grid
        Create(0, "EDIT", string.Empty, WsChild | WsVisible | WsBorder | WsTabStop, 10, 10, 120, 24, fields); // volume
        Create(0, "EDIT", string.Empty, WsChild | WsVisible | WsBorder | WsTabStop, 140, 10, 80, 24, fields); // unit
        var components = Create(0, PanelClass, string.Empty, WsChild | WsVisible, 10, 100, 490, 200, _form);
        _grid = Create(0, GridClass, string.Empty, WsChild | WsVisible | WsBorder | WsTabStop, 0, 0, 480, 190, components);
        Rows.Add(["", "", ""]);
        Publish();

        // Like FórmulaCerta after "Novo Orç.": the components grid has the focus.
        SetForegroundWindow(_form);
        SetFocus(_grid);
    }

    /// <summary>
    /// The quote screen with "Funcionário Recepção" already open, as when a person clicked elsewhere
    /// before the robot started. Shown with <c>--layout-variant win32-quote-asking</c>.
    /// </summary>
    public static void ShowAsking()
    {
        Show();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _employeeAsked = true;
            ShowModal(EmployeeDialogClass, "Funcionário Recepção", null, withEdit: true, "OK", "Cancelar");
        };
        timer.Start();
    }

    private static IntPtr GridWindowProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (handle != _grid)
        {
            return DefWindowProc(handle, message, wParam, lParam);
        }

        switch (message)
        {
            case WmGetDlgCode:
                return DlgcWantAllKeys | DlgcWantChars;
            case WmLButtonDown:
                SetFocus(handle);
                return IntPtr.Zero;
            case WmChar when wParam.ToInt32() >= 32:
                Cell.Append((char)wParam.ToInt32());
                Publish();
                return IntPtr.Zero;
            case WmKeyDown when wParam.ToInt32() == VkReturn:
                Enter();
                return IntPtr.Zero;
            case WmKillFocus when !_employeeAsked:
                // FórmulaCerta asks for the reception employee from inside the focus change.
                _employeeAsked = true;
                ShowModal(EmployeeDialogClass, "Funcionário Recepção", null, withEdit: true, "OK", "Cancelar");
                return IntPtr.Zero;
        }

        return DefWindowProc(handle, message, wParam, lParam);
    }

    private static void Enter()
    {
        var row = Rows[^1];
        if (_column == 0 && Cell.Length == 0 && row.All(cell => cell.Length == 0))
        {
            // Enter on the empty row saves the quote in FórmulaCerta.
            _saveAttempted = true;
            Publish();
            ShowModal(SaveDialogClass, "Salvando", "Q. S. P. ?", withEdit: false, "Sim", "Não");
            return;
        }

        row[_column] = Cell.ToString();
        Cell.Clear();
        _column++;
        if (_column == row.Length)
        {
            _column = 0;
            Rows.Add(["", "", ""]);
            Publish();
            if (row[0] == "999")
            {
                ShowModal(MessageClass, "Atenção!", "Produto abaixo do Estoque Mínimo!", withEdit: false, "OK");
            }
            else if (row[0] == "666")
            {
                ShowModal(MessageClass, "Atenção!", "Interação medicamentosa encontrada!", withEdit: false, "OK");
            }

            return;
        }

        Publish();
    }

    /// <summary>The grid's state as its window text: employee, complete rows, whether saving was attempted.</summary>
    private static void Publish()
    {
        var complete = Rows.Where(row => row.All(cell => cell.Length > 0)).Select(row => string.Join('|', row));
        SetWindowText(_grid, $"emp={_employee};rows={string.Join(';', complete)}{(_saveAttempted ? ";SAVE" : string.Empty)}");
    }

    /// <summary>A modal dialog like a Delphi form's ShowModal: the form is disabled and a nested loop runs until a button closes it.</summary>
    private static void ShowModal(string className, string title, string? text, bool withEdit, params string[] buttons)
    {
        var dialog = Create(WsExTopmost | WsExDlgModalFrame, className, title, WsPopup | WsCaption | WsSysMenu | WsVisible, 160, 160, 360, 150, IntPtr.Zero);
        if (text is not null)
        {
            Create(0, "STATIC", text, WsChild | WsVisible, 16, 16, 320, 24, dialog);
        }

        _dialogEdit = withEdit ? Create(0, "EDIT", string.Empty, WsChild | WsVisible | WsBorder | WsTabStop, 16, 16, 100, 24, dialog) : IntPtr.Zero;
        for (var index = 0; index < buttons.Length; index++)
        {
            Create(0, "BUTTON", buttons[index], WsChild | WsVisible | WsTabStop, 16 + index * 110, 70, 100, 28, dialog);
        }

        // Like Delphi's ShowModal (DisableTaskWindows): every other window of the program is disabled.
        var disabled = new List<IntPtr>();
        EnumThreadWindows(GetCurrentThreadId(), (window, _) =>
        {
            if (window != dialog && IsWindowVisible(window) && IsWindowEnabled(window))
            {
                EnableWindow(window, false);
                disabled.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        SetForegroundWindow(dialog);
        while (IsWindow(dialog) && GetMessage(out var loopMessage, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref loopMessage);
            DispatchMessage(ref loopMessage);
        }

        foreach (var window in disabled)
        {
            EnableWindow(window, true);
        }

        SetForegroundWindow(_form);
    }

    private static IntPtr DialogWindowProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
    {
        // BN_CLICKED (notification code 0) from a button; the edit's own notifications are ignored.
        if (message == WmCommand && lParam != IntPtr.Zero && lParam != _dialogEdit && (wParam.ToInt64() >> 16 & 0xFFFF) == 0)
        {
            // A button was pressed (BN_CLICKED): the employee dialog keeps what was typed.
            var caption = new StringBuilder(64);
            GetWindowText(lParam, caption, caption.Capacity);
            if (_dialogEdit != IntPtr.Zero && caption.ToString() == "OK")
            {
                var typed = new StringBuilder(64);
                GetWindowText(_dialogEdit, typed, typed.Capacity);
                _employee = typed.ToString();
                Publish();
            }

            DestroyWindow(handle);
            return IntPtr.Zero;
        }

        return DefWindowProc(handle, message, wParam, lParam);
    }

    private static IntPtr Create(int exStyle, string className, string text, uint style, int x, int y, int width, int height, IntPtr parent)
    {
        var handle = CreateWindowEx(exStyle, className, text, style, x, y, width, height, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx({className}) failed: {Marshal.GetLastWin32Error()}.");
        }

        if (parent != IntPtr.Zero && (style & WsChild) != 0)
        {
            SetWindowLongPtr(handle, -12, handle); // what VCL does for a child created without an ID
        }

        return handle;
    }

    private static void Register(string className, IntPtr procedure)
    {
        var windowClass = new WndClass
        {
            WindowProcedure = procedure != IntPtr.Zero ? procedure : GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            Instance = GetModuleHandle(null),
            Cursor = LoadCursor(IntPtr.Zero, 32512),
            Background = 15 + 1,
            ClassName = className,
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClass({className}) failed: {Marshal.GetLastWin32Error()}.");
        }
    }

    private delegate IntPtr WindowProcedure(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

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

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr handle, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr handle, bool enable);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr handle);

    private delegate bool EnumWindowsProcedure(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProcedure procedure, IntPtr parameter);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);
}
