using Prescriva.Agent.Application.Capture;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.Runtime;

/// <summary>
/// The production composition of <see cref="AgentRuntime"/>'s per-instance factories
/// (<see cref="TriggerProviderFactory"/>, <see cref="SelectorResolverFactory"/>,
/// <see cref="CaptureProviderFactory"/>) over real UI Automation. Every component it builds
/// shares one <see cref="AutomationDispatcher"/> (UIA calls stay on one STA thread) and is
/// scoped to the instance's own process ID, so two running copies of the same configured
/// application never resolve, capture or watch each other's windows.
///
/// The caller owns the dispatcher. The built components do not own it either, so they hold
/// no resource of their own that needs disposing beyond the dispatcher itself.
/// </summary>
public sealed class UiAutomationRuntimeFactories
{
    private readonly AutomationDispatcher _dispatcher;

    public UiAutomationRuntimeFactories(AutomationDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public ITriggerProvider CreateTriggerProvider(ApplicationInstance instance, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new UiAutomationTriggerProvider(_dispatcher, sessionId, processId: instance.ProcessId);
    }

    public ISelectorResolver CreateSelectorResolver(ApplicationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new UiAutomationSelectorResolver(_dispatcher, processId: instance.ProcessId);
    }

    public ICaptureProvider CreateCaptureProvider(ApplicationInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new UiAutomationCaptureProvider(_dispatcher);
    }
}
