using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Windows.Processes;

namespace Prescriva.Agent.Windows.IntegrationTests.RealApps;

/// <summary>
/// Starts real Windows applications for tests and closes them afterwards. Everything is found
/// through the open-window list and AutomationIds - never through (localized) names.
/// </summary>
internal sealed class RealApp : IDisposable
{
    private readonly HashSet<int> _preexisting;

    private RealApp(OpenWindowInfo window, HashSet<int> preexisting)
    {
        Window = window;
        _preexisting = preexisting;
        Element = FindTopLevel(window) ?? throw new InvalidOperationException($"No UI Automation window for '{window.WindowTitle}'.");
    }

    public OpenWindowInfo Window { get; }

    /// <summary>The top-level window (for Store apps, the ApplicationFrameHost frame holding the content).</summary>
    public AutomationElement Element { get; }

    /// <summary>
    /// Starts <paramref name="executable"/> and waits for a new window whose content process is
    /// one of <paramref name="processNames"/>; null when the application does not exist here.
    /// </summary>
    public static async Task<RealApp?> StartAsync(string executable, params string[] processNames)
    {
        var source = new Win32OpenWindowSource();
        var preexisting = (await source.ListAsync(CancellationToken.None)).Select(window => window.ProcessId).ToHashSet();
        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var window = (await source.ListAsync(CancellationToken.None)).FirstOrDefault(candidate =>
                !preexisting.Contains(candidate.ProcessId) &&
                processNames.Contains(candidate.ProcessName, StringComparer.OrdinalIgnoreCase));
            if (window is not null)
            {
                await Task.Delay(1000); // let the application finish building its UI
                return new RealApp(window, preexisting);
            }

            await Task.Delay(250);
        }

        return null;
    }

    public AutomationElement Find(string automationId) =>
        Element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
        ?? throw new InvalidOperationException($"'{automationId}' not found in '{Window.WindowTitle}'.");

    public void Press(string automationId) =>
        ((InvokePattern)Find(automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public void Dispose()
    {
        try
        {
            using var process = Process.GetProcessById(Window.ProcessId);
            if (!_preexisting.Contains(process.Id))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static AutomationElement? FindTopLevel(OpenWindowInfo window)
    {
        foreach (AutomationElement candidate in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            if (string.Equals(candidate.Current.Name, window.WindowTitle, StringComparison.Ordinal) &&
                (candidate.Current.ProcessId == window.ProcessId ||
                 candidate.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ProcessIdProperty, window.ProcessId)) is not null))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>The AutomationIds of the two Windows calculators: the classic one and the Store one.</summary>
internal sealed record CalculatorIds(string Flavor, string[] Digits, string EqualsButton, string Display)
{
    /// <summary>Classic Windows calculator (win32calc, e.g. Windows Server): Win32 control IDs.</summary>
    public static readonly CalculatorIds Classic = new("win32calc",
        ["130", "131", "132", "133", "134", "135", "136", "137", "138", "139"], "121", "150");

    /// <summary>Store calculator (CalculatorApp, Windows 10/11): XAML AutomationIds.</summary>
    public static readonly CalculatorIds Store = new("CalculatorApp",
        Enumerable.Range(0, 10).Select(digit => $"num{digit}Button").ToArray(), "equalButton", "CalculatorResults");

    public static readonly string[] ProcessNames = ["win32calc", "CalculatorApp", "Calculator"];

    public static CalculatorIds For(OpenWindowInfo window) =>
        string.Equals(window.ProcessName, "win32calc", StringComparison.OrdinalIgnoreCase) ? Classic : Store;
}
