using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Infrastructure.Playbooks;

namespace Prescriva.Agent.Infrastructure.Tests.Playbooks;

/// <summary>Playbooks are JSON files shipped with the Agent (plan 19); a file that is not a safe, valid playbook is refused.</summary>
public sealed class JsonPlaybookStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-playbooks-" + Guid.NewGuid().ToString("N"));

    public JsonPlaybookStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private const string ValidJson = """
        {
          "id": "lancar_formula",
          "name": "Lançar fórmula",
          "processIdentity": "fcreceitas",
          "windowRule": "Orçamentos",
          "steps": [
            { "id": "volume", "kind": "setField", "target": "TPanel[0]/TEdit[0]", "value": "{volume}" },
            { "id": "componentes", "kind": "gridRows", "target": "TPanel[1]/TGrid[0]", "rows": "componentes", "cells": ["{codigo}", "{quantidade}", "{unidade}"] }
          ],
          "dialogs": [
            { "id": "estoque_minimo", "className": "TMessageForm", "titleStartsWith": "Atenção", "textContains": "Estoque Mínimo", "response": "press", "button": "OK" }
          ]
        }
        """;

    [Fact]
    public async Task A_valid_file_is_loaded()
    {
        var path = Write("lancar_formula.json", ValidJson);

        var playbook = await JsonPlaybookStore.LoadAsync(path, CancellationToken.None);

        Assert.Equal("lancar_formula", playbook.Id);
        Assert.Equal(PlaybookStepKind.GridRows, playbook.Steps[1].Kind);
        Assert.Equal(["{codigo}", "{quantidade}", "{unidade}"], playbook.Steps[1].Cells.ToArray());
        Assert.Equal(DialogResponse.Press, playbook.Dialogs[0].Response);
    }

    [Fact]
    public async Task A_repeated_property_is_refused()
    {
        var path = Write("repetido.json", ValidJson.Replace("\"button\": \"OK\"", "\"button\": \"OK\", \"button\": \"Imprimir\"", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<PlaybookFileException>(() => JsonPlaybookStore.LoadAsync(path, CancellationToken.None));

        Assert.Equal(["MALFORMED_JSON"], error.Codes);
    }

    [Fact]
    public async Task An_unknown_property_is_refused()
    {
        var path = Write("desconhecido.json", ValidJson.Replace("\"button\": \"OK\"", "\"button\": \"OK\", \"pressAnyway\": true", StringComparison.Ordinal));

        await Assert.ThrowsAsync<PlaybookFileException>(() => JsonPlaybookStore.LoadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task A_playbook_that_would_press_a_forbidden_button_is_refused_with_the_reason()
    {
        var path = Write("imprimir.json", ValidJson.Replace("\"button\": \"OK\"", "\"button\": \"Imprimir\"", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<PlaybookFileException>(() => JsonPlaybookStore.LoadAsync(path, CancellationToken.None));

        Assert.Contains("FORBIDDEN_BUTTON", error.Codes);
    }

    [Fact]
    public void Run_data_is_read_from_json()
    {
        var data = JsonPlaybookStore.ReadData("""
            { "values": { "volume": "60", "unidade": "CAP" },
              "lists": { "componentes": [ { "codigo": "25531", "quantidade": "75", "unidade": "MG" } ] } }
            """);

        Assert.Equal("60", data.Values["volume"]);
        Assert.Equal("25531", data.Lists["componentes"][0]["codigo"]);
    }

    [Fact]
    public async Task Every_playbook_shipped_with_the_Agent_is_valid()
    {
        var shipped = Path.Combine(RepositoryRoot(), "src", "Prescriva.Agent.Desktop", "Playbooks");
        var files = Directory.GetFiles(shipped, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var playbook = await JsonPlaybookStore.LoadAsync(file, CancellationToken.None);
            Assert.Equal(Path.GetFileNameWithoutExtension(file), playbook.Id);
        }
    }

    private string Write(string name, string json)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, json);
        return path;
    }

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
