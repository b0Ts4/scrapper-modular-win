using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Tests.Configuration;

/// <summary>
/// A recent-file field (plan 16: the attachment a program has just opened) keeps where its file
/// is found as part of the configuration: saved, reloaded and folded into the content hash.
/// </summary>
public sealed class RecentFileFieldTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-recent-file-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Where_the_file_is_found_is_part_of_the_content_hash()
    {
        var first = WithSource(new FileSource("%LOCALAPPDATA%\\Temp", "anexo_*", 60));
        var second = WithSource(new FileSource("%LOCALAPPDATA%\\Temp", "outro_*", 60));

        Assert.NotEqual(ConfigurationFingerprint.Compute(first), ConfigurationFingerprint.Compute(second));
    }

    [Fact]
    public async Task A_recent_file_field_round_trips_through_the_json_store()
    {
        var store = new JsonConfigurationStore(_directory);
        var configuration = WithSource(new FileSource("%LOCALAPPDATA%\\Temp", "anexo_*", 120));

        await store.SaveAsync(configuration, CancellationToken.None);
        var json = await File.ReadAllTextAsync(Path.Combine(_directory, "receita.json"));
        var loaded = await store.LoadAsync("receita", CancellationToken.None);

        Assert.Contains("\"kind\": \"recentFile\"", json, StringComparison.Ordinal);
        Assert.Equal(new FileSource("%LOCALAPPDATA%\\Temp", "anexo_*", 120), loaded.Fields[0].FileSource);
        Assert.Equal(ConfigurationFingerprint.Compute(configuration), ConfigurationFingerprint.Compute(loaded));
    }

    private static IntegrationConfiguration WithSource(FileSource source) => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "receita",
        "Receita",
        new ApplicationDefinition("FCReceitas.exe", "Orçamentos"),
        [new FieldDefinition("anexo", "principal", "Anexo", false,
            new ElementFingerprint("FCMobyPharma.exe", "Visualização de Anexos", ClassName: "TScrollBox", WindowPath: "TPanel[0]/TScrollBox[0]"),
            FieldKind.RecentFile, source)],
        [new StageDefinition("principal", "Principal")],
        [new TriggerDefinition("nova_formula", "principal", new ElementFingerprint("FCMobyPharma.exe", "Visualização de Anexos", AutomationId: "x"), "Invoke",
            [new CaptureFieldsAction(["anexo"])])]);
}
