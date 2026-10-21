using System.Collections.Immutable;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Desktop.Playbooks;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Application.Tests.Playbooks;

/// <summary>
/// The pharmacy chooses, notice by notice, what the robot does (plan 20): stop and call a person,
/// or press one of the notice's buttons. Choices become the pharmacy's dialog policy.
/// </summary>
public sealed class NoticeSettingsViewModelTests
{
    [Fact]
    public void Every_playbook_notice_and_every_notice_the_robot_met_is_listed_with_its_current_choice()
    {
        var settings = new NoticeSettingsViewModel(Playbook(), policy: null, [Seen("Interação medicamentosa encontrada!")]);

        Assert.Equal(["Funcionário Recepção", "Atenção — Estoque Mínimo", "Atenção!"], settings.Rows.Select(row => row.Title));
        Assert.Equal("Preencher automaticamente", settings.Rows[0].Choice);
        Assert.False(settings.Rows[0].CanChange);
        Assert.Equal("Apertar «OK»", settings.Rows[1].Choice);
        Assert.Equal("Parar e chamar uma pessoa", settings.Rows[2].Choice);
        Assert.Equal(["Parar e chamar uma pessoa", "Apertar «OK»"], settings.Rows[2].Choices);
    }

    [Fact]
    public void Changing_a_playbook_notice_writes_a_rule_with_its_id()
    {
        var settings = new NoticeSettingsViewModel(Playbook(), policy: null, []);

        settings.Rows[1].Choice = "Parar e chamar uma pessoa";
        var policy = settings.BuildPolicy();

        var rule = Assert.Single(policy.Rules);
        Assert.Equal("estoque_minimo", rule.Id);
        Assert.Equal(DialogResponse.Stop, rule.Response);
        Assert.Equal("Estoque Mínimo", rule.TextContains);
    }

    [Fact]
    public void Answering_a_notice_the_robot_met_writes_a_rule_matching_its_longest_line()
    {
        var settings = new NoticeSettingsViewModel(Playbook(), policy: null, [Seen("Atenção! | x\nInteração medicamentosa encontrada!\nok")]);

        settings.Rows[2].Choice = "Apertar «OK»";
        var policy = settings.BuildPolicy();

        var rule = Assert.Single(policy.Rules);
        Assert.Equal(DialogResponse.Press, rule.Response);
        Assert.Equal("OK", rule.Button);
        Assert.Equal("TMessageForm", rule.ClassName);
        Assert.Equal("Atenção!", rule.TitleStartsWith);
        Assert.Equal("Interação medicamentosa encontrada!", rule.TextContains);
        Assert.False(string.IsNullOrWhiteSpace(rule.Id));
        Assert.Empty(PlaybookValidator.Validate(Playbook().WithPolicy(policy)));
    }

    [Fact]
    public void Leaving_everything_as_it_is_writes_no_rules()
    {
        var settings = new NoticeSettingsViewModel(Playbook(), policy: null, [Seen("Interação")]);

        Assert.Empty(settings.BuildPolicy().Rules);
    }

    [Fact]
    public void A_saved_choice_is_shown_and_a_seen_notice_covered_by_it_is_not_listed_twice()
    {
        var policy = new DialogPolicy("lancar_formula", [new DialogRule("aviso_1", "TMessageForm", "Atenção!", "Interação medicamentosa", DialogResponse.Press, "OK")]);

        var settings = new NoticeSettingsViewModel(Playbook(), policy, [Seen("Interação medicamentosa encontrada!")]);

        Assert.Equal(3, settings.Rows.Count);
        Assert.Equal("Apertar «OK»", settings.Rows[0].Choice);
        Assert.Single(settings.BuildPolicy().Rules);
    }

    [Fact]
    public void Forbidden_buttons_are_never_offered()
    {
        var settings = new NoticeSettingsViewModel(Playbook(), policy: null, [Seen("Deseja imprimir?") with { Buttons = ["Imprimir", "Cancelar"] }]);

        Assert.Equal(["Parar e chamar uma pessoa", "Apertar «Cancelar»"], settings.Rows[2].Choices);
    }

    private static SeenNotice Seen(string text) => new("TMessageForm", "Atenção!", text, ["OK"], DateTimeOffset.Now, 1);

    private static Playbook Playbook() => new(
        "lancar_formula",
        "Lançar fórmula",
        "fcreceitas",
        "Orçamentos",
        [new PlaybookStep("volume", PlaybookStepKind.SetField, "TPanel[0]/TEdit[0]", "{volume}")],
        [
            new DialogRule("funcionario", "TfrPedeFuncionario", "Funcionário Recepção", Response: DialogResponse.FillAndPress, Button: "OK", Field: "TwwDBEdit[1]", Value: "{funcionario}"),
            new DialogRule("estoque_minimo", "TMessageForm", "Atenção", "Estoque Mínimo", DialogResponse.Press, "OK"),
        ]);
}
