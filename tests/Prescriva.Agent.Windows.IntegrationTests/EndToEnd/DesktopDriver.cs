using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.EndToEnd;

/// <summary>
/// Drives the real Prescriva.Agent.Desktop.exe through UI Automation (Invoke/Value/Selection
/// patterns for the Agent's own controls) and the real OS cursor (for pointer inspection),
/// playing the operator in the Desktop walkthrough tests.
/// </summary>
internal static class DesktopDriver
{
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Steps 1-4 of the walkthrough through the real Desktop UI: the TestTarget chosen from the
    /// open-program list, integration and stage, three required fields and the Add/Finish
    /// buttons selected with the real cursor, then saved. Leaves inspection stopped.
    /// </summary>
    internal static async Task ConfigureAndSaveMedicineIntegrationAsync(AutomationElement agent, TestTargetLauncher target)
    {
        await ChooseProgramAsync(agent, "Test Target", $"PID {target.Window.Current.ProcessId}");
        Assert.Equal("Prescriva.Agent.TestTarget.exe", Value(agent, "ProcessIdentityBox"));
        Assert.Equal("Prescriva Agent Test Target", Value(agent, "WindowRuleBox"));
        SetText(agent, "IntegrationIdBox", "walkthrough");
        SetText(agent, "IntegrationNameBox", "Walkthrough");
        Press(agent, "CreateIntegrationButton");
        SetText(agent, "StageIdBox", "budget");
        SetText(agent, "StageNameBox", "Orçamento");
        Press(agent, "AddStageButton");
        await WaitForTextAsync(agent, "StatusText", "Etapa \'budget\' adicionada");

        Press(agent, "StartInspectionButton");
        foreach (var (automationId, fieldId) in new[]
                 {
                     ("MedicationTextBox", "medication"),
                     ("ConcentrationTextBox", "concentration"),
                     ("QuantityTextBox", "quantity"),
                 })
        {
            await HoverAndConfirmAsync(agent, target, automationId);
            SetText(agent, "FieldSemanticIdBox", fieldId);
            SetText(agent, "FieldMeaningBox", fieldId);
            Press(agent, "AddFieldButton");
            await WaitForTextAsync(agent, "StatusText", $"Campo \'{fieldId}\' adicionado");
        }

        await HoverAndConfirmAsync(agent, target, "AddButton");
        SetText(agent, "TriggerSemanticIdBox", "add_item");
        SetCaptureFields(agent, "medication", "concentration", "quantity");
        SetText(agent, "TriggerEmitEventBox", "item_added");
        SelectComboItem(agent, "TriggerTerminalBox", "Nada");
        Press(agent, "AddTriggerButton");
        await WaitForTextAsync(agent, "StatusText", "Gatilho \'add_item\' adicionado com 2 ação(ões)");

        await HoverAndConfirmAsync(agent, target, "FinishButton");
        SetText(agent, "TriggerSemanticIdBox", "finish_budget");
        SetCaptureFields(agent);
        SetText(agent, "TriggerEmitEventBox", "");
        SelectComboItem(agent, "TriggerTerminalBox", "Finalizar sessão (budget_finished)");
        Press(agent, "AddTriggerButton");
        await WaitForTextAsync(agent, "StatusText", "Gatilho \'finish_budget\' adicionado com 1 ação(ões)");
        Press(agent, "StopInspectionButton");

        Press(agent, "SaveButton");
        await WaitForTextAsync(agent, "StatusText", "Salvo. Alterações não salvas: não");
    }

    /// <summary>
    /// Step 1: searches the open-program list, selects the one entry whose description
    /// contains <paramref name="itemText"/> (e.g. its PID) and uses it.
    /// </summary>
    internal static async Task ChooseProgramAsync(AutomationElement agent, string search, string itemText)
    {
        SetText(agent, "OpenWindowsSearchBox", search);
        AutomationElement? item = null;
        await WaitUntilAsync(
            () =>
            {
                // Read the list only while no refresh is running (the button is disabled meanwhile).
                if (!IsEnabled(agent, "RefreshWindowsButton"))
                {
                    return false;
                }

                var matches = ListItems(agent, "OpenWindowsList").Where(candidate => candidate.Current.Name.Contains(itemText, StringComparison.Ordinal)).ToArray();
                item = matches.Length == 1 ? matches[0] : null;
                if (item is null)
                {
                    Press(agent, "RefreshWindowsButton");
                }

                return item is not null;
            },
            () => $"program list for '{search}' shows: {string.Join(" | ", ListItems(agent, "OpenWindowsList").Select(candidate => candidate.Current.Name))}");
        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Press(agent, "UseSelectedWindowButton");
        await WaitForTextAsync(agent, "StatusText", "Programa escolhido");
    }

    /// <summary>
    /// Step 5: <c>Exportar eventos (CSV)...</c> through the real Windows save dialog (file name box
    /// "1001", Save button "1"), then waits for the Agent to report the export.
    /// </summary>
    internal static async Task ExportCsvAsync(AutomationElement agent, int processId, string path)
    {
        Press(agent, "ExportCsvButton");
        // A modal dialog shows in UI Automation under its owner window (or, on some systems, the desktop).
        var dialogCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.ClassNameProperty, "#32770"));
        AutomationElement? dialog = null;
        await WaitUntilAsync(
            () => (dialog = agent.FindFirst(TreeScope.Children, dialogCondition)
                ?? AutomationElement.RootElement.FindFirst(TreeScope.Children, dialogCondition)) is not null,
            () => "the save dialog did not open; Agent status: " + Text(agent, "StatusText"));
        AutomationElement? fileName = null;
        await WaitUntilAsync(
            () => (fileName = dialog!.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1001"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))) is not null,
            () => "the save dialog has no file name box");
        // The modern file dialog keeps its own copy of the name and ignores ValuePattern.SetValue
        // when saving, so type the path as a person would.
        fileName!.SetFocus();
        await WaitUntilAsync(
            () =>
            {
                SelectAllAndType(fileName, path);
                return string.Equals(((ValuePattern)fileName.GetCurrentPattern(ValuePattern.Pattern)).Current.Value, path, StringComparison.OrdinalIgnoreCase);
            },
            () => "the file name box shows: " + ((ValuePattern)fileName.GetCurrentPattern(ValuePattern.Pattern)).Current.Value);
        var save = dialog!.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        ((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        await WaitForTextAsync(agent, "StatusText", "exportado(s) para");
        Assert.Contains(path, Text(agent, "StatusText"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Focuses <paramref name="box"/>, selects its text (Ctrl+A) and types <paramref name="text"/> with real keyboard input.</summary>
    private static void SelectAllAndType(AutomationElement box, string text)
    {
        box.SetFocus();
        SendKeys([Key(VkControl, down: true), Key(VkA, down: true), Key(VkA, down: false), Key(VkControl, down: false)]);
        SendKeys(text.SelectMany(character => new[] { Unicode(character, down: true), Unicode(character, down: false) }).ToArray());
        Thread.Sleep(200);
    }

    private const ushort VkControl = 0x11;
    private const ushort VkA = 0x41;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    private static KeyboardInput Key(ushort virtualKey, bool down) =>
        new() { Type = InputKeyboard, Data = new KeyboardData { VirtualKey = virtualKey, Flags = down ? 0 : KeyEventKeyUp } };

    private static KeyboardInput Unicode(char character, bool down) =>
        new() { Type = InputKeyboard, Data = new KeyboardData { ScanCode = character, Flags = KeyEventUnicode | (down ? 0 : KeyEventKeyUp) } };

    private static void SendKeys(KeyboardInput[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardInput>()) != inputs.Length)
        {
            throw new InvalidOperationException("SendInput failed.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint Type;
        public KeyboardData Data;
    }

    /// <summary>KEYBDINPUT, padded to the size of the INPUT union (MOUSEINPUT is the largest member).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
        public uint Padding1;
        public uint Padding2;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInput[] inputs, int size);

    /// <summary>Opens a step of the Agent's guided configurator (Step1Tab ... Step5Tab).</summary>
    internal static void GoToStep(AutomationElement agent, string stepAutomationId) =>
        ((SelectionItemPattern)Find(agent, stepAutomationId).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    /// <summary>Step 3: checks exactly the "read these fields" boxes named, unchecking the others.</summary>
    internal static void SetCaptureFields(AutomationElement agent, params string[] fieldIds)
    {
        _ = Find(agent, "TriggerNameBox"); // brings step 3 forward
        var boxes = agent.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
            .Cast<AutomationElement>()
            .Where(box => box.Current.AutomationId.StartsWith("CaptureField_", StringComparison.Ordinal))
            .ToArray();
        foreach (var fieldId in fieldIds)
        {
            Assert.Contains(boxes, box => box.Current.AutomationId == "CaptureField_" + fieldId);
        }

        foreach (var box in boxes)
        {
            var wanted = fieldIds.Contains(box.Current.AutomationId["CaptureField_".Length..], StringComparer.Ordinal);
            var toggle = (TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern);
            if ((toggle.Current.ToggleState == ToggleState.On) != wanted)
            {
                toggle.Toggle();
            }
        }
    }

    /// <summary>Prepares and runs test mode while the "operator" presses Add and Finish, until the run completes.</summary>
    internal static async Task RunTestModeAsync(AutomationElement agent, TestTargetLauncher target)
    {
        Press(agent, "PrepareTestButton");
        await WaitForTextAsync(agent, "StatusText", "Teste preparado");
        await WaitForTextAsync(agent, "TestStatusText", "Pronto.");
        Press(agent, "RunTestButton");
        await PressUntilAsync(target, ["AddButton", "FinishButton"], () => Text(agent, "TestStatusText").StartsWith("Teste concluído", StringComparison.Ordinal));
    }

    /// <summary>Arranges the two windows side by side so the cursor over TestTarget is never over the Agent.</summary>
    internal static void ArrangeSideBySide(AutomationElement agent, TestTargetLauncher target)
    {
        Move(target.Window, 0, 0);
        Move(agent, 560, 0);
    }

    /// <summary>The Agent's technical log (IDs, codes, timings only), for failure messages.</summary>
    internal static string ReadTechnicalLog(string dataDirectory)
    {
        try
        {
            using var stream = new FileStream(
                Path.Combine(dataDirectory, "logs", "technical.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException exception)
        {
            return $"(unreadable: {exception.GetType().Name})";
        }
    }

    internal static Task HoverAndConfirmAsync(AutomationElement agent, TestTargetLauncher target, string automationId) =>
        HoverAndConfirmElementAsync(agent, Find(target.Window, automationId), $"AutomationId='{automationId}'", $"hover-{automationId}");

    /// <summary>
    /// Moves the real cursor over <paramref name="element"/> until the Agent's hover text
    /// contains <paramref name="expectedHover"/>, checks the highlight outline surrounds the
    /// element, then confirms the selection.
    /// </summary>
    internal static async Task HoverAndConfirmElementAsync(AutomationElement agent, AutomationElement element, string expectedHover, string screenshotName)
    {
        var rect = element.Current.BoundingRectangle;
        var x = (int)(rect.X + rect.Width / 2);
        var y = (int)(rect.Y + rect.Height / 2);

        await WaitUntilAsync(
            () =>
            {
                SetCursorPos(x, y);
                return Text(agent, "HoverStateText").Contains(expectedHover, StringComparison.Ordinal);
            },
            () => $"hovering ({x},{y}) expecting {expectedHover}; Agent shows: {Text(agent, "HoverStateText")}");

        // The highlight outline must be drawn exactly around the hovered control.
        await WaitUntilAsync(
            () => OverlayMatches(rect),
            () => $"overlay is at {DescribeOverlay()}, control is at {rect}");
        SaveScreenshot(screenshotName);

        Press(agent, "ConfirmSelectionButton");
        await WaitForTextAsync(agent, "ConfirmedSelectionText", expectedHover);
    }

    internal static Task WaitForEventsAsync(AutomationElement agent, string dataDirectory, int count) =>
        WaitUntilAsync(
            () => ListTexts(agent, "EventsList").Length >= count,
            () => string.Join(" | ", ListTexts(agent, "DiagnosticsList")) + " / log: " + ReadTechnicalLog(dataDirectory));

    internal static Task WaitForTextAsync(AutomationElement root, string automationId, string expected) =>
        WaitUntilAsync(
            () => Text(root, automationId).Contains(expected, StringComparison.Ordinal),
            () => $"'{automationId}' shows: {Text(root, automationId)}");

    internal static async Task WaitUntilAsync(Func<bool> condition, Func<string> describe)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(150);
        }

        Assert.True(condition(), "Timed out: " + describe());
    }

    internal static async Task PressUntilAsync(TestTargetLauncher target, string[] automationIds, Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(400);
            foreach (var automationId in automationIds)
            {
                Press(target.Window, automationId);
            }
        }

        Assert.True(done(), "The test-mode run never passed.");
    }

    internal static void SetMedicine(TestTargetLauncher target, string medication, string concentration, string quantity)
    {
        SetText(target.Window, "MedicationTextBox", medication);
        SetText(target.Window, "ConcentrationTextBox", concentration);
        SetText(target.Window, "QuantityTextBox", quantity);
    }

    /// <summary>
    /// Finds a control by AutomationId. In the Agent's step-by-step window a control on another
    /// step (or in a collapsed "advanced" section) is reached the way a person would: by opening
    /// that step and expanding the section.
    /// </summary>
    internal static AutomationElement Find(AutomationElement root, string automationId) =>
        TryFind(root, automationId)
        ?? FindOnAnotherStep(root, automationId)
        ?? throw new InvalidOperationException($"Element '{automationId}' not found.");

    private static AutomationElement? TryFind(AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    private static AutomationElement? FindOnAnotherStep(AutomationElement root, string automationId)
    {
        var steps = TryFind(root, "WizardSteps");
        if (steps is null)
        {
            return null;
        }

        if (ExpandSections(root) && TryFind(root, automationId) is { } inSection)
        {
            return inSection;
        }

        foreach (AutomationElement step in steps.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)))
        {
            ((SelectionItemPattern)step.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (TryFind(root, automationId) is { } found)
                {
                    return found;
                }

                if (ExpandSections(root) && TryFind(root, automationId) is { } expanded)
                {
                    return expanded;
                }

                Thread.Sleep(100);
            }
        }

        return null;
    }

    /// <summary>Expands the collapsed sections (Expanders, never combo boxes) on the visible step.</summary>
    private static bool ExpandSections(AutomationElement root)
    {
        var expandedAny = false;
        var sections = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
            new PropertyCondition(AutomationElement.IsExpandCollapsePatternAvailableProperty, true)));
        foreach (AutomationElement section in sections)
        {
            var pattern = (ExpandCollapsePattern)section.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            {
                pattern.Expand();
                expandedAny = true;
            }
        }

        return expandedAny;
    }

    internal static string Value(AutomationElement root, string automationId) =>
        ((ValuePattern)Find(root, automationId).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;

    internal static AutomationElement[] ListItems(AutomationElement root, string automationId) =>
        Find(root, automationId)
            .FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>()
            .ToArray();

    internal static void SetText(AutomationElement root, string automationId, string value) =>
        ((ValuePattern)Find(root, automationId).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    internal static void Press(AutomationElement root, string automationId) =>
        ((InvokePattern)Find(root, automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    internal static bool IsEnabled(AutomationElement root, string automationId) =>
        Find(root, automationId).Current.IsEnabled;

    internal static string Text(AutomationElement root, string automationId) =>
        Find(root, automationId).Current.Name ?? string.Empty;

    internal static string[] AllTexts(AutomationElement root) =>
        root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>()
            .Select(element => element.Current.Name ?? string.Empty)
            .ToArray();

    internal static string[] ListTexts(AutomationElement root, string automationId) =>
        Find(root, automationId)
            .FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>()
            .Select(item => string.Join(" ", new[] { item.Current.Name }
                .Concat(item.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(child => child.Current.Name))))
            .ToArray();

    internal static void SelectComboItem(AutomationElement root, string automationId, string itemName)
    {
        var combo = Find(root, automationId);
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        expand.Expand();
        var item = combo.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, itemName))
            ?? throw new InvalidOperationException($"Combo item '{itemName}' not found.");
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        expand.Collapse();
    }

    internal static void Move(AutomationElement window, double x, double y)
    {
        var transform = (TransformPattern)window.GetCurrentPattern(TransformPattern.Pattern);
        transform.Move(x, y);
    }

    private const string OverlayTitle = "Prescriva Agent Highlight Overlay";
    private const double OverlayTolerancePixels = 2;

    /// <summary>True when the Agent's overlay window is visible and covers <paramref name="control"/> (physical pixels).</summary>
    internal static bool OverlayMatches(System.Windows.Rect control)
    {
        var overlay = FindWindow(null, OverlayTitle);
        if (overlay == IntPtr.Zero || !IsWindowVisible(overlay) || !GetWindowRect(overlay, out var bounds))
        {
            return false;
        }

        return Math.Abs(bounds.Left - control.Left) <= OverlayTolerancePixels
            && Math.Abs(bounds.Top - control.Top) <= OverlayTolerancePixels
            && Math.Abs(bounds.Right - control.Right) <= OverlayTolerancePixels
            && Math.Abs(bounds.Bottom - control.Bottom) <= OverlayTolerancePixels;
    }

    private static string DescribeOverlay()
    {
        var overlay = FindWindow(null, OverlayTitle);
        if (overlay == IntPtr.Zero)
        {
            return "(no overlay window)";
        }

        GetWindowRect(overlay, out var bounds);
        return $"{bounds.Left},{bounds.Top} {bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top} visible={IsWindowVisible(overlay)}";
    }

    /// <summary>
    /// Saves a PNG of the primary screen (layered windows included, so the overlay shows)
    /// when PRESCRIVA_SCREENSHOT_DIR is set - CI uploads them for visual review.
    /// </summary>
    internal static void SaveScreenshot(string name)
    {
        var directory = Environment.GetEnvironmentVariable("PRESCRIVA_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".png");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                CaptureScreen(path);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Screenshot failed.", failure);
        }
    }

    private static void CaptureScreen(string path)
    {
        const int SmCxScreen = 0;
        const int SmCyScreen = 1;
        const int SrcCopy = 0x00CC0020;
        const int CaptureBlt = 0x40000000;

        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            BitBlt(memoryDc, 0, 0, width, height, screenDc, 0, 0, SrcCopy | CaptureBlt);
            SelectObject(memoryDc, previous);
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            Save(source, path);

            // A small crop of the area the walkthrough places TestTarget in, cheap enough
            // to print into the CI log when artifacts cannot be downloaded.
            var crop = new System.Windows.Media.Imaging.CroppedBitmap(
                source, new System.Windows.Int32Rect(0, 0, Math.Min(560, width), Math.Min(500, height)));
            Save(crop, Path.ChangeExtension(path, null) + "-crop.png");
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void Save(System.Windows.Media.Imaging.BitmapSource image, string path)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    /// <summary>Launches the built Prescriva.Agent.Desktop.exe with an isolated data directory.</summary>
    internal sealed class DesktopProcess : IDisposable
    {
        private readonly Process _process;
        private AutomationElement? _window;

        private DesktopProcess(Process process, AutomationElement? window)
        {
            _process = process;
            _window = window;
        }

        public AutomationElement Window => _window ??= WaitForWindow();

        public int ProcessId => _process.Id;

        public bool HasExited
        {
            get
            {
                _process.Refresh();
                return _process.HasExited;
            }
        }

        public int ExitCode => _process.ExitCode;

        /// <summary>True while the Agent shows a visible top-level window (false when it runs only in the tray).</summary>
        public bool HasVisibleWindow
        {
            get
            {
                _process.Refresh();
                return !_process.HasExited && _process.MainWindowHandle != IntPtr.Zero;
            }
        }

        public static DesktopProcess Launch(string dataDirectory, IReadOnlyDictionary<string, string>? environment = null)
        {
            var process = Start(dataDirectory, environment, arguments: null);
            var desktop = new DesktopProcess(process, null);
            _ = desktop.Window;
            return desktop;
        }

        /// <summary>Starts the Agent as "Iniciar com o Windows" does (<c>--background</c>); no window is expected.</summary>
        public static DesktopProcess LaunchBackground(string dataDirectory, IReadOnlyDictionary<string, string>? environment = null) =>
            new(Start(dataDirectory, environment, "--background"), null);

        /// <summary>Starts the Agent without waiting for a window (a second start is expected to hand over and exit).</summary>
        public static DesktopProcess LaunchWithoutWaiting(string dataDirectory, IReadOnlyDictionary<string, string>? environment = null) =>
            new(Start(dataDirectory, environment, arguments: null), null);

        public static string ExecutablePath
        {
            get
            {
                var testTargetPath = TestTargetLauncher.ResolveBuiltExecutablePath();
                var binDirectory = Path.GetDirectoryName(testTargetPath)!;
                return Path.GetFullPath(Path.Combine(
                    binDirectory, "..", "..", "..", "..", "Prescriva.Agent.Desktop", "bin",
                    new DirectoryInfo(binDirectory).Parent!.Name, "net10.0-windows10.0.19041.0", "Prescriva.Agent.Desktop.exe"));
            }
        }

        public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);

        private static Process Start(string dataDirectory, IReadOnlyDictionary<string, string>? environment, string? arguments)
        {
            var executablePath = ExecutablePath;
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("Build Prescriva.Agent.Desktop before running this test.", executablePath);
            }

            var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false };
            if (arguments is not null)
            {
                startInfo.ArgumentList.Add(arguments);
            }

            startInfo.Environment["PRESCRIVA_AGENT_DATA"] = dataDirectory;
            foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            {
                startInfo.Environment[name] = value;
            }

            return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the Agent.");
        }

        private AutomationElement WaitForWindow()
        {
            // Same allowance as TestTargetLauncher for a cold first start on CI.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                _process.Refresh();
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"Prescriva.Agent.Desktop exited early with code {_process.ExitCode}.");
                }

                if (_process.MainWindowHandle != IntPtr.Zero)
                {
                    return AutomationElement.FromHandle(_process.MainWindowHandle);
                }

                Thread.Sleep(100);
            }

            _process.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out waiting for the Prescriva.Agent.Desktop main window.");
        }

        public void Close()
        {
            if (HasExited)
            {
                return;
            }

            ((WindowPattern)Window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            if (!_process.WaitForExit(10_000))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(5_000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
