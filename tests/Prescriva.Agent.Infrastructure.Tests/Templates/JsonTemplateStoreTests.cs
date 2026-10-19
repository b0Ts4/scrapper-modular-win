using System.Collections.Immutable;
using Prescriva.Agent.Application.Templates;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Infrastructure.Templates;

namespace Prescriva.Agent.Infrastructure.Tests.Templates;

/// <summary>Templates are JSON files: shipped with the Agent (read-only) or saved by the person (plan 15).</summary>
public sealed class JsonTemplateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prescriva-templates-" + Guid.NewGuid().ToString("N"));

    private string BuiltIn => Path.Combine(_root, "builtin");

    private string Own => Path.Combine(_root, "own");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task An_own_template_is_saved_and_listed_after_the_shipped_ones()
    {
        await new JsonTemplateStore(Path.Combine(_root, "unused"), BuiltIn).SaveAsync(Template("de_fabrica", "De fábrica"), CancellationToken.None);
        var store = new JsonTemplateStore(BuiltIn, Own);

        await store.SaveAsync(Template("meu_modelo", "Meu modelo"), CancellationToken.None);
        var listed = await store.ListAsync(CancellationToken.None);

        Assert.Equal(["de_fabrica", "meu_modelo"], listed.Select(entry => entry.Template.Id));
        Assert.True(listed[0].IsBuiltIn);
        Assert.False(listed[1].IsBuiltIn);
        var reloaded = listed[1].Template;
        Assert.Equal("TPanel[0]/TEdit[1]", reloaded.Items[0].Suggested!.WindowPath);
        Assert.Equal(FieldKind.OcrText, reloaded.Items[1].FieldKind);
        Assert.Equal(["posologia"], reloaded.Items[2].CapturedFields.ToArray());
    }

    [Fact]
    public async Task An_own_template_with_the_id_of_a_shipped_one_replaces_it_in_the_list()
    {
        await new JsonTemplateStore(Path.Combine(_root, "x"), BuiltIn).SaveAsync(Template("modelo", "De fábrica"), CancellationToken.None);
        var store = new JsonTemplateStore(BuiltIn, Own);

        await store.SaveAsync(Template("modelo", "Ajustado por mim"), CancellationToken.None);
        var listed = await store.ListAsync(CancellationToken.None);

        var only = Assert.Single(listed);
        Assert.Equal("Ajustado por mim", only.Template.Name);
        Assert.False(only.IsBuiltIn);
    }

    [Fact]
    public async Task A_broken_file_is_left_out_instead_of_breaking_the_list()
    {
        Directory.CreateDirectory(Own);
        await File.WriteAllTextAsync(Path.Combine(Own, "quebrado.json"), "{ not json");
        var store = new JsonTemplateStore(BuiltIn, Own);
        await store.SaveAsync(Template("bom", "Bom"), CancellationToken.None);

        var listed = await store.ListAsync(CancellationToken.None);

        Assert.Equal(["bom"], listed.Select(entry => entry.Template.Id));
    }

    /// <summary>
    /// A property written twice ("fieldKind": "ocrText" then "screenImage") silently kept the last
    /// one and turned an OCR item into an image (found on FórmulaCerta): such a file is refused.
    /// </summary>
    [Fact]
    public async Task A_template_file_with_a_repeated_property_is_left_out()
    {
        var store = new JsonTemplateStore(BuiltIn, Own);
        await store.SaveAsync(Template("bom", "Bom"), CancellationToken.None);
        var json = await File.ReadAllTextAsync(Path.Combine(Own, "bom.json"));
        var repeated = json.Replace("\"fieldKind\": \"ocrText\"", "\"fieldKind\": \"ocrText\", \"fieldKind\": \"screenImage\"", StringComparison.Ordinal);
        Assert.NotEqual(json, repeated);
        await File.WriteAllTextAsync(Path.Combine(Own, "repetido.json"), repeated.Replace("\"id\": \"bom\"", "\"id\": \"repetido\"", StringComparison.Ordinal));

        var listed = await store.ListAsync(CancellationToken.None);

        Assert.Equal(["bom"], listed.Select(entry => entry.Template.Id));
    }

    [Fact]
    public async Task An_invalid_template_is_not_saved()
    {
        var store = new JsonTemplateStore(BuiltIn, Own);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Template("ok", " "), CancellationToken.None));
        Assert.False(Directory.Exists(Own) && Directory.EnumerateFiles(Own).Any());
    }

    [Fact]
    public async Task Every_template_shipped_with_the_Agent_is_valid()
    {
        var shipped = Path.Combine(RepositoryRoot(), "src", "Prescriva.Agent.Desktop", "Templates");
        var files = Directory.GetFiles(shipped, "*.json");
        Assert.NotEmpty(files);

        var listed = await new JsonTemplateStore(shipped, Own).ListAsync(CancellationToken.None);

        Assert.Equal(files.Length, listed.Count);
        Assert.All(listed, entry => Assert.Empty(TemplateValidator.Validate(entry.Template)));
    }

    /// <summary>FórmulaCerta's Orçamentos OK is not a detectable click (plan 17): the shipped quote templates send on the "Mais Fórmula(s)?" question, once per formula.</summary>
    [Theory]
    [InlineData("formulacerta_orcamento")]
    [InlineData("formulacerta_receita_completa")]
    public async Task The_shipped_quote_templates_send_when_the_more_formulas_question_appears(string id)
    {
        var shipped = Path.Combine(RepositoryRoot(), "src", "Prescriva.Agent.Desktop", "Templates");
        var listed = await new JsonTemplateStore(shipped, Own).ListAsync(CancellationToken.None);

        var question = listed.Single(entry => entry.Template.Id == id).Template.Items.Single(item => item.Id == "mais_formulas");

        Assert.Equal(TemplateTriggerEvent.WindowShown, question.Observe);
        Assert.Equal("TMessageForm", question.Suggested!.ClassName);
        Assert.Equal("Orçamento ", question.Suggested.Name);
        Assert.Equal(TemplateButtonRole.SendAndKeep, question.Role);
        Assert.Equal(["numero_orcamento", "serie_formula"], question.UniqueBy.ToArray());
    }

    private static IntegrationTemplate Template(string id, string name) => new(
        id,
        name,
        "Explicação.",
        new TemplateProgram("erp.exe", "Orçamento", "Menu Saídas."),
        [
            new TemplateItem("posologia", TemplateItemKind.Field, "Posologia", "Campo Posologia.",
                Suggested: new ElementFingerprint(string.Empty, string.Empty, ClassName: "TEdit", FrameworkId: "Win32", WindowPath: "TPanel[0]/TEdit[1]")),
            new TemplateItem("componentes", TemplateItemKind.Field, "Componentes", "A grade.", FieldKind.OcrText, Required: false),
            new TemplateItem("ok", TemplateItemKind.Button, "OK", "Botão OK.", CapturedFields: ImmutableArray.Create("posologia")),
        ]);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Prescriva.Agent.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Prescriva.Agent.slnx not found.");
    }
}
