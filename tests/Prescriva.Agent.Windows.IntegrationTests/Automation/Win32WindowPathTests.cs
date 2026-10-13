using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// Delphi (VCL) controls - e.g. the FórmulaCerta pharmacy system - have their window handle as
/// AutomationId, which changes on every start, and no label UI Automation can see. TestTarget's
/// <c>win32-form</c> variant builds such a window. A field marked once must still be found and
/// read after the program restarts (plan 14).
/// </summary>
public sealed class Win32WindowPathTests
{
    private const string FormTitle = "Prescriva Win32 Form";

    [Fact]
    public async Task A_Delphi_like_field_is_found_and_read_again_after_the_program_restarts()
    {
        ElementFingerprint fingerprint;
        using (var first = TestTargetLauncher.Launch("win32-form"))
        using (var dispatcher = new AutomationDispatcher())
        {
            var quantity = Edits(WaitForForm(first))[2];
            Assert.Equal(quantity.Current.NativeWindowHandle.ToString(System.Globalization.CultureInfo.InvariantCulture), quantity.Current.AutomationId);

            fingerprint = await InspectAsync(first, dispatcher, quantity);

            Assert.Null(fingerprint.AutomationId);
            Assert.False(string.IsNullOrEmpty(fingerprint.WindowPath));
        }

        using var second = TestTargetLauncher.Launch("win32-form");
        using var secondDispatcher = new AutomationDispatcher();
        WaitForForm(second);
        using var resolver = new UiAutomationSelectorResolver(secondDispatcher, processId: second.Window.Current.ProcessId);

        var resolution = await resolver.ResolveAsync(fingerprint, CancellationToken.None);

        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);
        using var capture = new UiAutomationCaptureProvider(secondDispatcher);
        var result = await capture.CaptureAsync(
            resolution.Handle!,
            new FieldDefinition("quantity", "budget", "Quantidade", Required: true, Selector: fingerprint),
            CancellationToken.None);
        Assert.Equal(CaptureOutcome.Captured, result.Outcome);
        Assert.Equal("2", result.Value);
    }

    /// <summary>
    /// Some Delphi controls are named by their value (FórmulaCerta's date boxes); a configuration
    /// never holds captured values, so such a control's name is not recorded. (A vendor button
    /// that UI Automation reports as a Button keeps its caption - seen on FórmulaCerta; this
    /// fixture's button is reported as a Pane, so it cannot show that.)
    /// </summary>
    [Fact]
    public async Task A_Delphi_like_control_named_by_its_value_is_marked_without_its_name()
    {
        using var target = TestTargetLauncher.Launch("win32-form");
        using var dispatcher = new AutomationDispatcher();
        var date = WaitForForm(target).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "TPrescrivaDate"));
        Assert.NotNull(date);
        Assert.Equal("14/07/26", date!.Current.Name);

        var fingerprint = await InspectAsync(target, dispatcher, date, "TPrescrivaDate");

        Assert.Null(fingerprint.Name);
        Assert.False(string.IsNullOrEmpty(fingerprint.WindowPath));
    }

    /// <summary>
    /// A vendor button (FórmulaCerta's TFagronButton; TestTarget's TPrescrivaButton) raises no
    /// UI Automation Invoke event; a click shows only as the button capturing and releasing the mouse.
    /// </summary>
    [Fact]
    public async Task A_click_on_a_Delphi_like_button_without_an_Invoke_event_is_detected()
    {
        using var target = TestTargetLauncher.Launch("win32-form");
        using var dispatcher = new AutomationDispatcher();
        var button = WaitForForm(target).FindFirst(
            TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "TPrescrivaButton"));
        Assert.NotNull(button);
        var fingerprint = await InspectAsync(target, dispatcher, button, "TPrescrivaButton");
        Assert.False(string.IsNullOrEmpty(fingerprint.WindowPath));

        using var provider = new UiAutomationTriggerProvider(dispatcher, Guid.NewGuid(), processId: target.Window.Current.ProcessId);
        var watched = new TaskCompletionSource();
        provider.WatchEstablished += (_, _) => watched.TrySetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var trigger = new TriggerDefinition("add", "budget", fingerprint, "Invoke", [new EmitEventAction("item_added")]);
        var firstSignal = FirstSignalAsync(provider, trigger, cts.Token);

        await watched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rect = button!.Current.BoundingRectangle;
        RealMouse.Click((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));

        var signal = await firstSignal;
        Assert.NotNull(signal);
        Assert.Equal("add", signal!.TriggerId);
    }

    private static async Task<Prescriva.Agent.Application.Triggers.TriggerSignal?> FirstSignalAsync(
        UiAutomationTriggerProvider provider, TriggerDefinition trigger, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var signal in provider.WatchAsync(trigger, cancellationToken))
            {
                return signal;
            }
        }
        catch (OperationCanceledException)
        {
        }

        return null;
    }

    private static Task<ElementFingerprint> InspectAsync(TestTargetLauncher target, AutomationDispatcher dispatcher, AutomationElement element) =>
        InspectAsync(target, dispatcher, element, "Edit");

    private static async Task<ElementFingerprint> InspectAsync(TestTargetLauncher target, AutomationDispatcher dispatcher, AutomationElement element, string className)
    {
        var rect = element.Current.BoundingRectangle;
        var point = new ScreenPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var controller = new InspectionController(new UiAutomationElementInspector(dispatcher));
        await controller.StartAsync();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await controller.ObservePointerAsync(point);
            if (controller.CurrentState.Snapshot is { } snapshot && snapshot.ClassName == className &&
                snapshot.ProcessId == target.Window.Current.ProcessId)
            {
                break;
            }

            await Task.Delay(200);
        }

        var confirmed = await controller.ConfirmAsync();
        Assert.Equal(className, confirmed.Snapshot!.ClassName);
        return confirmed.Fingerprint!;
    }

    /// <summary>A real left click through the OS input queue (the button must see WM_LBUTTONDOWN/UP).</summary>
    private static class RealMouse
    {
        private const uint InputMouse = 0;
        private const uint LeftDown = 0x0002;
        private const uint LeftUp = 0x0004;

        public static void Click(int x, int y)
        {
            SetCursorPos(x, y);
            Thread.Sleep(100);
            Send(LeftDown);
            Thread.Sleep(80);
            Send(LeftUp);
        }

        private static void Send(uint flags)
        {
            var input = new Input { Type = InputMouse, Mouse = new MouseInput { Flags = flags } };
            if (SendInput(1, [input], System.Runtime.InteropServices.Marshal.SizeOf<Input>()) != 1)
            {
                throw new InvalidOperationException("SendInput failed.");
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public MouseInput Mouse;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);
    }

    private static AutomationElement[] Edits(AutomationElement form) =>
        form.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "Edit"))
            .Cast<AutomationElement>()
            .OrderBy(edit => edit.Current.BoundingRectangle.Y)
            .ToArray();

    private static AutomationElement WaitForForm(TestTargetLauncher target)
    {
        var processId = target.Window.Current.ProcessId;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            var form = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                    new PropertyCondition(AutomationElement.NameProperty, FormTitle)));
            if (form is not null)
            {
                return form;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException($"The '{FormTitle}' window did not appear.");
    }
}
