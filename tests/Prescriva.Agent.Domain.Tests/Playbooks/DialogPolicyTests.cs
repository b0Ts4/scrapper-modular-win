using System.Collections.Immutable;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Domain.Tests.Playbooks;

/// <summary>Each pharmacy decides what the robot does with each notice (plan 20): its rules are merged into the playbook's.</summary>
public sealed class DialogPolicyTests
{
    [Fact]
    public void A_pharmacy_rule_with_the_id_of_a_playbook_rule_replaces_it_in_place()
    {
        var policy = new DialogPolicy("lancar_formula", [new DialogRule("estoque_minimo", "TMessageForm", "Atenção", "Estoque Mínimo", DialogResponse.Stop)]);

        var merged = PlaybookValidatorTests.Valid().WithPolicy(policy);

        Assert.Equal(["funcionario", "estoque_minimo", "quantidade_zerada"], merged.Dialogs.Select(rule => rule.Id));
        Assert.Equal(DialogResponse.Stop, merged.Dialogs[1].Response);
    }

    [Fact]
    public void New_pharmacy_rules_come_before_the_playbook_rules()
    {
        var policy = new DialogPolicy("lancar_formula", [new DialogRule("interacao", "TMessageForm", "Atenção", "Interação", DialogResponse.Press, "OK")]);

        var merged = PlaybookValidatorTests.Valid().WithPolicy(policy);

        Assert.Equal("interacao", merged.Dialogs[0].Id);
        Assert.Equal(4, merged.Dialogs.Length);
    }

    [Fact]
    public void No_policy_leaves_the_playbook_as_it_is()
    {
        var playbook = PlaybookValidatorTests.Valid();

        Assert.Same(playbook, playbook.WithPolicy(null));
    }

    [Fact]
    public void A_policy_cannot_make_the_robot_press_a_forbidden_button()
    {
        var policy = new DialogPolicy("lancar_formula", [new DialogRule("estoque_minimo", "TMessageForm", "Atenção", null, DialogResponse.Press, "Imprimir")]);

        var merged = PlaybookValidatorTests.Valid().WithPolicy(policy);

        Assert.Contains(PlaybookValidator.Validate(merged), error => error.Code == "FORBIDDEN_BUTTON");
    }

    [Fact]
    public void A_read_step_needs_the_name_to_keep_the_value_under()
    {
        var playbook = PlaybookValidatorTests.Valid() with { Steps = [new PlaybookStep("numero", PlaybookStepKind.ReadField, "TPanel[0]/TEdit[2]")] };

        Assert.Contains(PlaybookValidator.Validate(playbook), error => error.Code == "MISSING_CAPTURE_NAME");
        Assert.Empty(PlaybookValidator.Validate(playbook with { Steps = [playbook.Steps[0] with { CaptureAs = "numero_orcamento" }] }));
    }

    [Fact]
    public void A_button_may_come_from_the_data_but_a_capture_field_needs_its_name()
    {
        var playbook = PlaybookValidatorTests.Valid() with
        {
            Dialogs =
            [
                new DialogRule("qsp", "TMessageForm", "Componente", "Q. S. P.", DialogResponse.Press, "{qsp}"),
                new DialogRule("pagamento", "TfrPagamento", "Condições", Response: DialogResponse.Press, Button: "OK", CaptureField: "TwwDBEdit[0]"),
            ],
        };

        var errors = PlaybookValidator.Validate(playbook);

        Assert.Single(errors);
        Assert.Equal("MISSING_CAPTURE_NAME", errors[0].Code);
    }
}
