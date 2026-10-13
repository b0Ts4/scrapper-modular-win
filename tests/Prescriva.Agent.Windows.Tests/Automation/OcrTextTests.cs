using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.Tests.Automation;

public sealed class OcrTextTests
{
    [Fact]
    public void Brazilian_Portuguese_is_preferred_when_installed()
    {
        Assert.Equal("pt-BR", OcrText.ChooseLanguage(["en-US", "pt-BR", "pt-PT"], ["en-US"]));
    }

    [Fact]
    public void Another_Portuguese_comes_next()
    {
        Assert.Equal("pt-PT", OcrText.ChooseLanguage(["en-US", "pt-PT"], ["en-US"]));
    }

    [Fact]
    public void Without_Portuguese_the_users_profile_language_is_used()
    {
        Assert.Equal("es-ES", OcrText.ChooseLanguage(["en-US", "es-ES"], ["es-MX", "en-US"]));
    }

    [Fact]
    public void Otherwise_the_first_installed_recognizer_is_used_and_none_means_unavailable()
    {
        Assert.Equal("en-US", OcrText.ChooseLanguage(["en-US", "fr-FR"], ["de-DE"]));
        Assert.Null(OcrText.ChooseLanguage([], ["pt-BR"]));
    }

    /// <summary>
    /// A grid (FórmulaCerta's components): the recognizer returns every cell as its own line.
    /// Cells at the same height are one row, read left to right and kept apart by " | ".
    /// </summary>
    [Fact]
    public void Cells_side_by_side_become_one_row_in_reading_order()
    {
        OcrText.PlacedLine[] lines =
        [
            new("Quantidade", X: 300, Y: 10, Height: 12),
            new("Descrição", X: 80, Y: 11, Height: 12),
            new("DIPIRONA 500 MG", X: 80, Y: 31, Height: 12),
            new("10 g", X: 302, Y: 30, Height: 13),
            new("CAFEÍNA", X: 80, Y: 50, Height: 12),
            new("2 g", X: 300, Y: 51, Height: 12),
        ];

        Assert.Equal("Descrição | Quantidade\nDIPIRONA 500 MG | 10 g\nCAFEÍNA | 2 g", OcrText.Rows(lines));
    }

    [Fact]
    public void Text_one_line_under_another_stays_as_it_was()
    {
        OcrText.PlacedLine[] lines = [new("  Tomar  1x ", 5, 0, 12), new("ao dia", 5, 20, 12)];

        Assert.Equal("Tomar 1x\nao dia", OcrText.Rows(lines));
    }

    [Fact]
    public void Lines_are_trimmed_collapsed_and_kept_apart()
    {
        Assert.Equal("DIPIRONA 500 MG\nTomar 1x ao dia", OcrText.Normalize(["  DIPIRONA   500 MG ", "", "Tomar\t1x ao dia"]));
        Assert.Equal(string.Empty, OcrText.Normalize([" ", ""]));
    }
}
