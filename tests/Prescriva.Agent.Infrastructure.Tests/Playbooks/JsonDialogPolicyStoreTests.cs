using System.Collections.Immutable;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Infrastructure.Playbooks;

namespace Prescriva.Agent.Infrastructure.Tests.Playbooks;

/// <summary>A pharmacy's answers to notices and the notices the robot met, kept per playbook in the Agent's data (plan 20).</summary>
public sealed class JsonDialogPolicyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-policies-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_saved_policy_is_loaded_back()
    {
        var store = new JsonDialogPolicyStore(_directory);
        var policy = new DialogPolicy("lancar_formula", [new DialogRule("estoque_minimo", "TMessageForm", "Atenção", "Estoque Mínimo", DialogResponse.Stop)]);

        await store.SaveAsync(policy, CancellationToken.None);
        var loaded = await new JsonDialogPolicyStore(_directory).LoadAsync("lancar_formula", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(DialogResponse.Stop, Assert.Single(loaded!.Rules).Response);
    }

    [Fact]
    public async Task No_file_means_no_policy_and_no_seen_notices()
    {
        var store = new JsonDialogPolicyStore(_directory);

        Assert.Null(await store.LoadAsync("lancar_formula", CancellationToken.None));
        Assert.Empty(await store.SeenAsync("lancar_formula", CancellationToken.None));
    }

    [Fact]
    public async Task A_notice_met_twice_is_kept_once_with_how_many_times_and_its_title_start()
    {
        var store = new JsonDialogPolicyStore(_directory);
        var first = new UnknownDialog("TMessageForm", "Orçamento 899124", ["Sim", "Não"], "Deseja imprimir?");
        var again = first with { Title = "Orçamento 899125" };

        await store.RecordSeenAsync("lancar_formula", first, CancellationToken.None);
        await store.RecordSeenAsync("lancar_formula", again, CancellationToken.None);
        var seen = await store.SeenAsync("lancar_formula", CancellationToken.None);

        var notice = Assert.Single(seen);
        Assert.Equal("Orçamento ", notice.TitleStartsWith); // the quote number is not part of the notice
        Assert.Equal(2, notice.Times);
        Assert.Equal(["Sim", "Não"], notice.Buttons.ToArray());
    }

    [Fact]
    public async Task Saving_a_policy_keeps_the_seen_notices()
    {
        var store = new JsonDialogPolicyStore(_directory);
        await store.RecordSeenAsync("lancar_formula", new UnknownDialog("TMessageForm", "Atenção!", ["OK"], "Interação"), CancellationToken.None);

        await store.SaveAsync(new DialogPolicy("lancar_formula", []), CancellationToken.None);

        Assert.Single(await store.SeenAsync("lancar_formula", CancellationToken.None));
    }

    [Fact]
    public async Task A_policy_file_with_an_unknown_property_is_refused()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "lancar_formula.avisos.json"), """{ "playbookId": "lancar_formula", "rules": [], "seen": [], "extra": 1 }""");

        await Assert.ThrowsAsync<PlaybookFileException>(() => new JsonDialogPolicyStore(_directory).LoadAsync("lancar_formula", CancellationToken.None));
    }

    [Theory]
    [InlineData("../fora")]
    [InlineData("a b")]
    public async Task A_playbook_id_that_is_not_a_plain_name_is_refused(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new JsonDialogPolicyStore(_directory).LoadAsync(id, CancellationToken.None));
    }
}
