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

    [Fact]
    public void Lines_are_trimmed_collapsed_and_kept_apart()
    {
        Assert.Equal("DIPIRONA 500 MG\nTomar 1x ao dia", OcrText.Normalize(["  DIPIRONA   500 MG ", "", "Tomar\t1x ao dia"]));
        Assert.Equal(string.Empty, OcrText.Normalize([" ", ""]));
    }
}
