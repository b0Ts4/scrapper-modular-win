using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Inspection;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Shared top-level window lookup and descendant enumeration used by both
/// <see cref="UiAutomationElementInspector"/> and <see cref="UiAutomationSelectorResolver"/>.
/// Must only ever be called from the dispatcher's dedicated STA thread, same as every
/// other real UI Automation call in this namespace.
/// </summary>
internal static class AutomationWindowLocator
{
    /// <summary>
    /// Enumerates every descendant of <paramref name="window"/>, mapping the one place
    /// both callers see the window disappear mid-enumeration
    /// (<see cref="ElementNotAvailableException"/>) onto the same typed
    /// <see cref="ElementInspectionFailureKind.WindowMissing"/> failure, instead of each
    /// caller wrapping its own identical `FindAll` + try/catch shell.
    /// </summary>
    public static IReadOnlyList<AutomationElement> FindDescendants(AutomationElement window)
    {
        try
        {
            var collection = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var elements = new List<AutomationElement>(collection.Count);
            foreach (AutomationElement descendant in collection)
            {
                elements.Add(descendant);
            }

            return elements;
        }
        catch (ElementNotAvailableException ex)
        {
            throw new ElementInspectionFailure(
                ElementInspectionFailureKind.WindowMissing,
                "The target window closed while enumerating its elements.",
                ex);
        }
    }

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
