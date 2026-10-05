using System.Windows.Automation;
using Prescriva.Agent.Application.Selection;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// The concrete, real handle produced by <see cref="UiAutomationSelectorResolver"/>. Not
/// public: only code inside Prescriva.Agent.Windows.Automation can construct one or read
/// <see cref="Element"/> back out, so Application-layer code holding the base
/// <see cref="ResolvedElementHandle"/> type has no way to reach the native
/// <see cref="AutomationElement"/> it wraps.
/// </summary>
internal sealed class UiaResolvedElementHandle : ResolvedElementHandle
{
    internal UiaResolvedElementHandle(AutomationElement element)
    {
        Element = element;
    }

    // Deliberately `internal`, not `public`: a `public` property on an `internal` type is
    // still reachable via reflection with default (public) BindingFlags from any assembly
    // - `Type.GetProperty` finds public *members* regardless of the declaring type's own
    // accessibility. `internal` raises the bar to a deliberate BindingFlags.NonPublic
    // reflection call, which is the level of opacity this boundary is meant to provide.
    internal AutomationElement Element { get; }
}
