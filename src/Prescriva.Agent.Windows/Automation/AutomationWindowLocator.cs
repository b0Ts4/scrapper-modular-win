using System.Diagnostics;
using System.Windows.Automation;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Shared top-level window lookup used by both <see cref="UiAutomationElementInspector"/>
/// and <see cref="UiAutomationSelectorResolver"/>. Must only ever be called from the
/// dispatcher's dedicated STA thread, same as every other real UI Automation call in this
/// namespace.
/// </summary>
internal static class AutomationWindowLocator
{
    public static AutomationElement? Find(string processIdentity, string windowRule)
    {
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            string processName;
            try
            {
                processName = Process.GetProcessById(window.Current.ProcessId).ProcessName;
            }
            catch (ArgumentException)
            {
                // The process behind this top-level window has already exited.
                continue;
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            if (!string.Equals(processName, processIdentity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(window.Current.Name, windowRule, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }

        return null;
    }
}
