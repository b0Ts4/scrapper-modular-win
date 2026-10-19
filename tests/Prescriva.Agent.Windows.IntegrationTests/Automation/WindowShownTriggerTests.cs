using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// FórmulaCerta's Orçamentos OK never captures the mouse: it opens its questions at once, and
/// "Mais Fórmula(s)?" (a TMessageForm titled "Orçamento &lt;n&gt;") is the moment the formula is
/// saved. A trigger on a window that appears (plan 17) fires for a top-level window of the
/// program with the configured class whose title starts with the configured text - and only
/// for it. TestTarget's <c>win32-questions</c> variant shows "Outro aviso 1" and "Orçamento 42"
/// in turn, one per second.
/// </summary>
public sealed class WindowShownTriggerTests
{
    private const string Process = "Prescriva.Agent.TestTarget.exe";

    [Fact]
    public async Task A_question_window_of_the_program_whose_title_starts_with_the_rule_fires_the_trigger()
    {
        using var target = TestTargetLauncher.Launch("win32-questions");
        using var dispatcher = new AutomationDispatcher();
        // As for an item of another program: not scoped to one process, the program is told by its name.
        using var provider = new UiAutomationTriggerProvider(dispatcher, Guid.NewGuid());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var signal = await FirstSignalAsync(provider, Question("Orçamento "), cts.Token);

        Assert.NotNull(signal);
        Assert.Equal("mais_formulas", signal!.TriggerId);
    }

    [Fact]
    public async Task Windows_with_another_title_or_class_do_not_fire_the_trigger()
    {
        using var target = TestTargetLauncher.Launch("win32-questions");
        using var dispatcher = new AutomationDispatcher();
        using var provider = new UiAutomationTriggerProvider(dispatcher, Guid.NewGuid());
        using var otherTitle = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var otherClass = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        var byTitle = FirstSignalAsync(provider, Question("Requisição "), otherTitle.Token);
        var byClass = FirstSignalAsync(provider, Question("Orçamento ") with
        {
            Selector = Question("Orçamento ").Selector with { ClassName = "TMessageForm" },
        }, otherClass.Token);

        Assert.Null(await byTitle);
        Assert.Null(await byClass);
    }

    private static TriggerDefinition Question(string titleStart) => new(
        "mais_formulas",
        "principal",
        new ElementFingerprint(Process, "Prescriva Win32 Form", Name: titleStart, ClassName: "TPrescrivaQuestion", FrameworkId: "Win32"),
        TriggerEvents.WindowShown,
        [new EmitEventAction("receita")]);

    private static async Task<TriggerSignal?> FirstSignalAsync(UiAutomationTriggerProvider provider, TriggerDefinition trigger, CancellationToken cancellationToken)
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
}
