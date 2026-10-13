using System.Collections.Immutable;
using Prescriva.Agent.Application.Templates;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Tests.Templates;

/// <summary>Templates (plan 15): what a valid template is and how a suggestion is aimed at the chosen program.</summary>
public sealed class IntegrationTemplateTests
{
    [Fact]
    public void A_template_with_fields_and_a_button_reading_them_is_valid()
    {
        Assert.Empty(TemplateValidator.Validate(Sample()));
    }

    [Fact]
    public void Item_ids_must_be_semantic_ids_and_unique()
    {
        var template = Sample() with
        {
            Items = Sample().Items.Add(new TemplateItem("posologia", TemplateItemKind.Field, "Outra", "x"))
                .Add(new TemplateItem("1-inválido", TemplateItemKind.Field, "Inválido", "x")),
        };

        var errors = TemplateValidator.Validate(template);

        Assert.Contains(errors, error => error.Contains("posologia", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("1-inválido", StringComparison.Ordinal));
    }

    [Fact]
    public void A_button_may_only_read_fields_of_the_template()
    {
        var template = Sample() with
        {
            Items = Sample().Items.Add(new TemplateItem("salvar", TemplateItemKind.Button, "Salvar", "x", CapturedFields: ["nao_existe"])),
        };

        Assert.Contains(TemplateValidator.Validate(template), error => error.Contains("nao_existe", StringComparison.Ordinal));
    }

    [Fact]
    public void A_template_needs_a_name_and_at_least_one_item()
    {
        var errors = TemplateValidator.Validate(Sample() with { Name = " ", Items = [] });

        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void A_suggested_position_is_aimed_at_the_chosen_program_and_window()
    {
        var item = Sample().Items[0];

        var selector = item.SuggestionFor(new ApplicationDefinition("FCReceitas.exe", "FórmulaCerta - Orçamentos 6.0.2000"));

        Assert.NotNull(selector);
        Assert.Equal("FCReceitas.exe", selector!.ProcessIdentity);
        Assert.Equal("FórmulaCerta - Orçamentos 6.0.2000", selector.WindowRule);
        Assert.Equal("TPanel[0]/TwwDBComboBox[2]", selector.WindowPath);
    }

    [Fact]
    public void An_item_without_a_suggestion_has_none()
    {
        Assert.Null(Sample().Items[1].SuggestionFor(new ApplicationDefinition("x.exe", "X")));
    }

    [Fact]
    public void The_window_matches_the_template_by_process_and_part_of_its_title()
    {
        var program = Sample().Program;

        Assert.True(program.Matches("FCReceitas", "FórmulaCerta - Orçamentos 6.0.1164"));
        Assert.True(program.Matches("fcreceitas.exe", "FÓRMULACERTA - ORÇAMENTOS"));
        Assert.False(program.Matches("FCReceitas", "FórmulaCerta - Receitas 6.0.1164"));
        Assert.False(program.Matches("outro", "FórmulaCerta - Orçamentos"));
    }

    internal static IntegrationTemplate Sample() => new(
        "formulacerta_orcamento",
        "FórmulaCerta – Orçamento",
        "Lê a posologia e os componentes.",
        new TemplateProgram("FCReceitas.exe", "Orçamentos", "No FórmulaCerta: Saídas > Orçamento."),
        [
            new TemplateItem("posologia", TemplateItemKind.Field, "Posologia", "Aba Fórmula, campo Posologia.",
                Suggested: new ElementFingerprint(string.Empty, string.Empty, ClassName: "TwwDBComboBox", FrameworkId: "Win32", WindowPath: "TPanel[0]/TwwDBComboBox[2]")),
            new TemplateItem("prescritor_nome", TemplateItemKind.Field, "Nome do prescritor", "Linha Prescritor, campo longo."),
            new TemplateItem("ok", TemplateItemKind.Button, "OK", "Botão OK.", CapturedFields: ["posologia", "prescritor_nome"]),
        ]);
}
