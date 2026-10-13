using Prescriva.Agent.Application.Configuration;

namespace Prescriva.Agent.Application.Tests.Configuration;

/// <summary>Internal IDs come from the friendly names people type; they are always valid and unique.</summary>
public sealed class SemanticIdGeneratorTests
{
    [Theory]
    [InlineData("Medicamento", "medicamento")]
    [InlineData("Concentração do remédio", "concentracao_do_remedio")]
    [InlineData("  Quantidade (caixas)  ", "quantidade_caixas")]
    [InlineData("Botão Adicionar!", "botao_adicionar")]
    [InlineData("2ª via", "campo_2a_via")]
    [InlineData("###", "campo")]
    public void A_friendly_name_becomes_a_valid_id(string name, string expected)
    {
        var id = SemanticIdGenerator.FromName(name, []);

        Assert.Equal(expected, id);
        Assert.Matches("^[A-Za-z][A-Za-z0-9_]*$", id);
    }

    [Fact]
    public void An_id_already_taken_gets_a_number_ignoring_letter_case()
    {
        Assert.Equal("medicamento_2", SemanticIdGenerator.FromName("Medicamento", ["MEDICAMENTO"]));
        Assert.Equal("medicamento_3", SemanticIdGenerator.FromName("medicamento", ["medicamento", "medicamento_2"]));
    }

    [Fact]
    public void A_custom_prefix_is_used_for_names_that_do_not_start_with_a_letter()
    {
        Assert.Equal("botao_1", SemanticIdGenerator.FromName("1", [], fallbackPrefix: "botao"));
    }
}
