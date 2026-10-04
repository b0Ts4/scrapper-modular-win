using System.Diagnostics;
using System.Windows.Automation;
using Prescriva.Agent.Application.Inspection;
using Prescriva.Agent.Windows.Processes;

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

    /// <summary>
    /// Finds the single top-level window matching <paramref name="processIdentity"/> (by
    /// process name) and <paramref name="windowRule"/> (by window title).
    /// </summary>
    /// <param name="processId">
    /// When supplied, additionally requires the window's owning process ID to match
    /// exactly - the only way to tell apart two running instances of the same configured
    /// application (identical process name, identical/matching window title), where a
    /// name+title match alone cannot determine which physical window is the right one.
    /// Left <see langword="null"/> (the default), behavior is unchanged from before this
    /// parameter existed: the first window whose process name and title match is returned,
    /// in whatever order <see cref="AutomationElement.RootElement"/> enumerates its
    /// children - existing single-instance callers keep working exactly as today.
    /// </param>
    public static AutomationElement? Find(string processIdentity, string windowRule, int? processId = null)
    {
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            string processName;
            int windowProcessId;
            try
            {
                windowProcessId = window.Current.ProcessId;
                processName = Process.GetProcessById(windowProcessId).ProcessName;
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

            if (!ProcessIdentity.Matches(processIdentity, processName))
            {
                continue;
            }

            if (processId.HasValue && windowProcessId != processId.Value)
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
