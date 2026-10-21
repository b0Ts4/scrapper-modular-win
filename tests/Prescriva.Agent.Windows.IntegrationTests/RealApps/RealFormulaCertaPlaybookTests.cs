using System.Collections.Immutable;
using System.IO;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Infrastructure.Playbooks;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.RealApps;

/// <summary>
/// The shipped playbook on the real FórmulaCerta (plans 19-20). Opt-in: runs only with
/// <c>PRESCRIVA_FORMULACERTA_PLAYBOOK=1</c>, on the pharmacy's PC, with a new quote already opened
/// by a person in the Orçamentos screen (Novo Orç. → automático). Test data only (simulated
/// formula, employee 19). With <c>PRESCRIVA_FORMULACERTA_SAVE=1</c> the quote is saved and its
/// number read back; otherwise the playbook stops before OK and the person cancels the quote.
/// The pharmacy's notice choices are used, and a notice the robot stops on is remembered there
/// (the Agent's data directory), as the Agent itself does.
/// </summary>
public sealed class RealFormulaCertaPlaybookTests
{
    [Fact]
    public async Task The_shipped_playbook_fills_a_new_quote()
    {
        if (Environment.GetEnvironmentVariable("PRESCRIVA_FORMULACERTA_PLAYBOOK") != "1")
        {
            return;
        }

        var save = Environment.GetEnvironmentVariable("PRESCRIVA_FORMULACERTA_SAVE") == "1";
        var policies = new JsonDialogPolicyStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prescriva", "Agent", "playbooks"));
        var shipped = await JsonPlaybookStore.LoadAsync(ShippedPlaybook(), CancellationToken.None);
        var playbook = shipped.WithPolicy(await policies.LoadAsync(shipped.Id, CancellationToken.None));
        var data = new PlaybookData(
            ImmutableDictionary<string, string>.Empty
                .Add("volume", "60").Add("volume_unidade", "CAP").Add("funcionario", "19")
                .Add("salvar", save ? "sim" : "não").Add("qsp", "Não").Add("mais_formulas", "Não"),
            ImmutableDictionary<string, ImmutableArray<ImmutableDictionary<string, string>>>.Empty.Add("componentes",
            [
                Row("25531", "75", "MG"), // PREGABALINA
                Row("7995", "5", "MG"),   // CICLOBENZAPRINA (HCL)
            ]));

        var result = await new PlaybookRunner(new Win32PlaybookDriver()).RunAsync(playbook, data, CancellationToken.None);
        if (result.Unknown is { } unknown)
        {
            await policies.RecordSeenAsync(shipped.Id, unknown, CancellationToken.None);
        }

        Assert.True(result.Status == PlaybookRunStatus.Completed,
            $"{result.Status} at {result.StepId}: {result.Reason} {result.Detail} (answered: {string.Join(", ", result.AnsweredDialogs)})" +
            (result.Unknown is { } seen ? $"\nunknown: {seen.ClassName} '{seen.Title}' [{string.Join(", ", seen.Buttons)}] text: {seen.Text}" : string.Empty) +
            $"\ncaptured: {string.Join(", ", result.Captured.Select(pair => pair.Key + "=" + pair.Value))}");
        if (save)
        {
            Assert.False(string.IsNullOrWhiteSpace(result.Captured.GetValueOrDefault("numero_orcamento")), "The quote number was not read back.");
        }
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
