using System.Collections.Immutable;
using System.IO;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Infrastructure.Playbooks;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.RealApps;

/// <summary>
/// The shipped playbook on the real FórmulaCerta (plan 19, task 5). Opt-in: runs only with
/// <c>PRESCRIVA_FORMULACERTA_PLAYBOOK=1</c>, on the pharmacy's PC, with a new quote already opened
/// by a person in the Orçamentos screen (Novo Orç. → automático). Test data only (simulated
/// formula, employee 19); the playbook stops before OK and the person cancels the quote.
/// </summary>
public sealed class RealFormulaCertaPlaybookTests
{
    [Fact]
    public async Task The_shipped_playbook_fills_a_new_quote_and_stops_before_saving()
    {
        if (Environment.GetEnvironmentVariable("PRESCRIVA_FORMULACERTA_PLAYBOOK") != "1")
        {
            return;
        }

        var playbook = await JsonPlaybookStore.LoadAsync(ShippedPlaybook(), CancellationToken.None);
        var data = new PlaybookData(
            ImmutableDictionary<string, string>.Empty.Add("volume", "60").Add("volume_unidade", "CAP").Add("funcionario", "19"),
            ImmutableDictionary<string, ImmutableArray<ImmutableDictionary<string, string>>>.Empty.Add("componentes",
            [
                Row("25531", "75", "MG"), // PREGABALINA
                Row("7995", "5", "MG"),   // CICLOBENZAPRINA (HCL)
            ]));

        var result = await new PlaybookRunner(new Win32PlaybookDriver()).RunAsync(playbook, data, CancellationToken.None);

        Assert.True(result.Status == PlaybookRunStatus.Completed,
            $"{result.Status} at {result.StepId}: {result.Reason} {result.Detail} (answered: {string.Join(", ", result.AnsweredDialogs)})");
    }

    private static ImmutableDictionary<string, string> Row(string code, string quantity, string unit) =>
        ImmutableDictionary<string, string>.Empty.Add("codigo", code).Add("quantidade", quantity).Add("unidade", unit);

    private static string ShippedPlaybook()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prescriva.Agent.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "src", "Prescriva.Agent.Desktop", "Playbooks", "formulacerta_lancar_formula.json");
    }
}
