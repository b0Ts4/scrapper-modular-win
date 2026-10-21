using System.Collections.Immutable;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Domain.Tests.Playbooks;

/// <summary>A playbook drives a program's screen (plan 19): it is refused when it could do something unsafe or cannot run.</summary>
public sealed class PlaybookValidatorTests
{
    [Fact]
    public void A_complete_playbook_is_valid()
    {
        Assert.Empty(PlaybookValidator.Validate(Valid()));
    }

    [Fact]
    public void Step_ids_and_dialog_rule_ids_are_unique()
    {
        var playbook = Valid();
        playbook = playbook with
        {
            Steps = playbook.Steps.Add(playbook.Steps[0]),
            Dialogs = playbook.Dialogs.Add(playbook.Dialogs[0]),
        };

        var codes = PlaybookValidator.Validate(playbook).Select(error => error.Code).ToArray();

        Assert.Contains("DUPLICATE_STEP_ID", codes);
        Assert.Contains("DUPLICATE_DIALOG_RULE_ID", codes);
    }

    [Fact]
    public void A_set_field_step_needs_a_target_and_a_value()
    {
        var playbook = Valid() with { Steps = [new PlaybookStep("volume", PlaybookStepKind.SetField, " ")] };

        var codes = PlaybookValidator.Validate(playbook).Select(error => error.Code).ToArray();

        Assert.Contains("MISSING_TARGET", codes);
        Assert.Contains("MISSING_VALUE", codes);
    }

    [Fact]
    public void A_grid_step_needs_the_list_name_and_every_cell()
    {
        var playbook = Valid() with { Steps = [new PlaybookStep("componentes", PlaybookStepKind.GridRows, "TPanel[0]/TGrid[0]", Cells: ["{code}", " "])] };

        var codes = PlaybookValidator.Validate(playbook).Select(error => error.Code).ToArray();

        Assert.Contains("MISSING_ROWS", codes);
        Assert.Contains("EMPTY_CELL_TEMPLATE", codes);
    }

    [Fact]
    public void A_template_with_an_unclosed_or_empty_placeholder_is_refused()
    {
        var playbook = Valid() with { Steps = [new PlaybookStep("volume", PlaybookStepKind.SetField, "TPanel[0]/TEdit[0]", "{volume")] };

        Assert.Contains(PlaybookValidator.Validate(playbook), error => error.Code == "INVALID_TEMPLATE");
        Assert.Contains(PlaybookValidator.Validate(playbook with { Steps = [playbook.Steps[0] with { Value = "{}" }] }), error => error.Code == "INVALID_TEMPLATE");
    }

    [Fact]
    public void A_rule_cannot_press_a_forbidden_button()
    {
        var playbook = Valid() with { Dialogs = [new DialogRule("imprimir", "TForm", "Impress", Response: DialogResponse.Press, Button: "Imprimir")] };

        Assert.Contains(PlaybookValidator.Validate(playbook), error => error.Code == "FORBIDDEN_BUTTON");
    }

    [Fact]
    public void Forbidden_buttons_are_compared_without_accents_letter_case_or_accelerator()
    {
        var playbook = Valid() with { Dialogs = [new DialogRule("req", "TForm", "Req", Response: DialogResponse.Press, Button: "&REQUISIÇAO")] };

        Assert.Contains(PlaybookValidator.Validate(playbook), error => error.Code == "FORBIDDEN_BUTTON");
    }

    [Fact]
    public void A_press_rule_needs_a_button_and_a_fill_rule_needs_its_field_and_value()
    {
        var playbook = Valid() with
        {
            Dialogs =
            [
                new DialogRule("a", "TForm", "A", Response: DialogResponse.Press),
                new DialogRule("b", "TForm", "B", Response: DialogResponse.FillAndPress, Button: "OK"),
            ],
        };

        var codes = PlaybookValidator.Validate(playbook).Select(error => error.Code).ToArray();

        Assert.Contains("MISSING_BUTTON", codes);
        Assert.Contains("MISSING_DIALOG_FIELD", codes);
    }

    [Fact]
    public void A_rule_must_name_the_window_class_and_title()
    {
        var playbook = Valid() with { Dialogs = [new DialogRule("a", " ", "", Response: DialogResponse.Stop)] };

        Assert.Contains(PlaybookValidator.Validate(playbook), error => error.Code == "INCOMPLETE_DIALOG_RULE");
    }

    [Fact]
    public void A_playbook_must_name_its_program()
    {
        var playbook = Valid() with { ProcessIdentity = " " };

        Assert.Contains(PlaybookValidator.Validate(playbook), error => error.Code == "MISSING_PROGRAM");
    }

    [Fact]
    public void The_default_forbidden_buttons_cover_printing_requisitions_sales_and_deletion()
    {
        foreach (var caption in new[] { "Imprimir", "Requisição", "Finalizar", "Excluir", "Aprovar", "Vender" })
        {
            Assert.True(Playbook.IsForbidden(caption, Playbook.DefaultForbiddenButtons), caption);
        }

        Assert.False(Playbook.IsForbidden("OK", Playbook.DefaultForbiddenButtons));
    }

    internal static Playbook Valid() => new(
        "lancar_formula",
        "Lançar fórmula",
        "fcreceitas",
        "Orçamentos",
        [
            new PlaybookStep("volume", PlaybookStepKind.SetField, "TPanel[0]/TEdit[0]", "{volume}"),
            new PlaybookStep("componentes", PlaybookStepKind.GridRows, "TPanel[0]/TGrid[0]", Rows: "componentes", Cells: ["{codigo}", "{quantidade}", "{unidade}"]),
        ],
        [
            new DialogRule("funcionario", "TfrFuncionario", "Funcionário Recepção", Response: DialogResponse.FillAndPress, Button: "OK", Field: "TEdit[1]", Value: "{funcionario}"),
            new DialogRule("estoque_minimo", "TMessageForm", "Atenção", "Estoque Mínimo", DialogResponse.Press, "OK"),
            new DialogRule("quantidade_zerada", "TfrAtencao", "Atenção", "Quantidade zerada", DialogResponse.Stop),
        ]);
}
