using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;
using Prescriva.Agent.Windows.Processes;

namespace Prescriva.Agent.Windows.IntegrationTests.Runtime;

/// <summary>
/// The configurator suggests the executable file name ("Prescriva.Agent.TestTarget.exe")
/// as the target process. Both real lookups the runtime depends on - instance discovery
/// and window location - must find the running TestTarget from that spelling.
/// </summary>
public sealed class ProcessIdentityDiscoveryTests
{
    private const string ExecutableIdentity = "Prescriva.Agent.TestTarget.exe";
    private const string WindowTitle = "Prescriva Agent Test Target";

    [Fact]
    public async Task Instance_source_discovers_the_running_target_from_its_executable_file_name()
    {
        using var target = TestTargetLauncher.Launch();
        var processId = target.Window.Current.ProcessId;
        var source = new WindowsApplicationInstanceSource(pollInterval: TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var discovered = false;
        await foreach (var change in source.WatchAsync(new ApplicationDefinition(ExecutableIdentity, WindowTitle), cts.Token))
        {
            if (change.Kind == ApplicationInstanceChangeKind.Started && change.Instance.ProcessId == processId)
            {
                discovered = true;
                break;
            }
        }

        Assert.True(discovered);
    }

    /// <summary>
    /// Plan 18: a window rule may be part of the title ("Orçamentos" for "FórmulaCerta -
    /// Orçamentos 6.0.1164"), so a program update that changes the version keeps working - also
    /// for a Delphi-like form that is not the process's main window (TestTarget's win32-form).
    /// </summary>
    [Theory]
    [InlineData(null, "Test Target")]
    [InlineData("win32-form", "Win32 Form")]
    public async Task Instance_source_discovers_the_program_from_part_of_its_window_title(string? variant, string titlePart)
    {
        using var target = variant is null ? TestTargetLauncher.Launch() : TestTargetLauncher.Launch(variant);
        var processId = target.Window.Current.ProcessId;
        var source = new WindowsApplicationInstanceSource(pollInterval: TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var discovered = false;
        try
        {
            await foreach (var change in source.WatchAsync(new ApplicationDefinition(ExecutableIdentity, titlePart), cts.Token))
            {
                if (change.Kind == ApplicationInstanceChangeKind.Started && change.Instance.ProcessId == processId)
                {
                    discovered = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(discovered);
    }

    [Fact]
    public async Task Selector_resolver_finds_an_element_in_the_window_named_by_part_of_its_title()
    {
        using var target = TestTargetLauncher.Launch();
        using var resolver = new UiAutomationSelectorResolver(processId: target.Window.Current.ProcessId);

        var resolution = await resolver.ResolveAsync(
            new ElementFingerprint(ExecutableIdentity, "test target", AutomationId: "AddButton", ControlType: "ControlType.Button"),
            CancellationToken.None);

        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);
    }

    [Fact]
    public async Task Selector_resolver_finds_an_element_whose_process_identity_is_an_executable_file_name()
    {
        using var target = TestTargetLauncher.Launch();
        var processId = target.Window.Current.ProcessId;
        using var resolver = new UiAutomationSelectorResolver(processId: processId);

        var resolution = await resolver.ResolveAsync(
            new ElementFingerprint(ExecutableIdentity, WindowTitle, AutomationId: "AddButton", ControlType: "ControlType.Button"),
            CancellationToken.None);

        Assert.Equal(SelectorResolutionStatus.Found, resolution.Status);
    }
}
