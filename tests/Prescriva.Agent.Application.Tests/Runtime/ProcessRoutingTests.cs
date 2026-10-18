using System.Runtime.CompilerServices;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Selection;
using Prescriva.Agent.Application.Triggers;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Tests.Runtime;

/// <summary>
/// One integration across two programs (plan 16): the session belongs to an instance of the
/// main program; a field or button of another program is resolved and watched in that program.
/// </summary>
public sealed class ProcessRoutingTests
{
    private static readonly ApplicationInstance Main = new(Guid.NewGuid(), 1234);

    [Fact]
    public async Task A_selector_of_the_main_program_goes_to_its_instance_and_another_program_to_any_instance()
    {
        var built = new List<ApplicationInstance>();
        var resolver = ProcessRouting.Resolver("FCReceitas.exe", instance =>
        {
            built.Add(instance);
            return new NamedResolver(instance.ProcessId == 0 ? "outro" : "principal");
        }, Main);

        var main = await resolver.ResolveAsync(new ElementFingerprint("fcreceitas", "Orçamentos"), CancellationToken.None);
        var other = await resolver.ResolveAsync(new ElementFingerprint("FCMobyPharma.exe", "Visualização de Anexos"), CancellationToken.None);

        Assert.Equal("principal", main.FailureReason);
        Assert.Equal("outro", other.FailureReason);
        Assert.Contains(Main, built);
        Assert.Contains(ApplicationInstance.AnyProcess, built);
    }

    [Fact]
    public async Task A_button_of_another_program_is_watched_there()
    {
        var watchers = new Dictionary<int, NamedTriggers>();
        var provider = ProcessRouting.Triggers("FCReceitas.exe", (instance, _) => watchers[instance.ProcessId] = new NamedTriggers(), Main, Guid.NewGuid());

        await foreach (var _ in provider.WatchAsync(Trigger("FCMobyPharma.exe"), CancellationToken.None)) { }
        await foreach (var _ in provider.WatchAsync(Trigger("FCReceitas.exe"), CancellationToken.None)) { }

        Assert.Equal(["nova_formula"], watchers[0].Watched);
        Assert.Equal(["nova_formula"], watchers[Main.ProcessId].Watched);
    }

    [Theory]
    [InlineData("FCReceitas.exe", "fcreceitas", true)]
    [InlineData("FCReceitas", "FCReceitas.EXE", true)]
    [InlineData("FCReceitas.exe", "FCMobyPharma.exe", false)]
    public void Process_names_are_compared_without_exe_and_letter_case(string first, string second, bool same)
    {
        Assert.Equal(same, ProcessRouting.SameProcess(first, second));
    }

    private static TriggerDefinition Trigger(string process) =>
        new("nova_formula", "principal", new ElementFingerprint(process, "Janela"), "Invoke", [new ClearStateAction()]);

    private sealed class NamedResolver(string name) : ISelectorResolver
    {
        public Task<SelectorResolution> ResolveAsync(ElementFingerprint fingerprint, CancellationToken cancellationToken) =>
            Task.FromResult(SelectorResolution.NotFound(name));
    }

    private sealed class NamedTriggers : ITriggerProvider
    {
        public List<string> Watched { get; } = [];

        public async IAsyncEnumerable<TriggerSignal> WatchAsync(TriggerDefinition trigger, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Watched.Add(trigger.Id);
            await Task.CompletedTask;
            yield break;
        }
    }
}
