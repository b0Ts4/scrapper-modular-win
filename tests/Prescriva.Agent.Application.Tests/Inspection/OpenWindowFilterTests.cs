using Prescriva.Agent.Application.Inspection;

namespace Prescriva.Agent.Application.Tests.Inspection;

/// <summary>Finding a program in the list of open windows by typing part of its name, title or process.</summary>
public sealed class OpenWindowFilterTests
{
    private static readonly OpenWindowInfo[] Windows =
    [
        new("Calculadora", "Calculadora", "CalculatorApp", 4100),
        new("Bloco de Notas", "Sem título - Bloco de Notas", "Notepad", 4200),
        new("SisFarma", "Orçamento nº 15 - SisFarma", "SisFarma", 4300),
    ];

    [Theory]
    [InlineData("calc", "CalculatorApp")]
    [InlineData("ORCAMENTO", "SisFarma")]
    [InlineData("notepad", "Notepad")]
    [InlineData("titulo", "Notepad")]
    public void The_search_ignores_accents_and_letter_case_across_app_title_and_process(string query, string expectedProcess)
    {
        var found = Assert.Single(OpenWindowFilter.Apply(Windows, query));

        Assert.Equal(expectedProcess, found.ProcessName);
    }

    [Fact]
    public void An_empty_search_lists_every_window_sorted_by_app_name()
    {
        Assert.Equal(["Bloco de Notas", "Calculadora", "SisFarma"], OpenWindowFilter.Apply(Windows, "  ").Select(w => w.AppName));
    }

    [Fact]
    public void A_search_with_no_match_returns_nothing()
    {
        Assert.Empty(OpenWindowFilter.Apply(Windows, "excel"));
    }
}
