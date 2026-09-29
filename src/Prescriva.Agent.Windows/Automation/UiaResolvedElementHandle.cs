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
    public UiaResolvedElementHandle(AutomationElement element)
    {
        Element = element;
    }

    public AutomationElement Element { get; }
}
