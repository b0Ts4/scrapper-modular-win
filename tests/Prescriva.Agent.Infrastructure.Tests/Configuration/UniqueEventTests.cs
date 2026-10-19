using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Tests.Configuration;

/// <summary>
/// An event sent once per value of some fields (plan 17) and a trigger on a window that
/// appears keep both settings in the configuration: saved, reloaded and in the content hash.
/// </summary>
public sealed class UniqueEventTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-unique-event-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Being_unique_by_fields_is_part_of_the_content_hash()
    {
        Assert.NotEqual(
            ConfigurationFingerprint.Compute(With(new EmitEventAction("receita"))),
            ConfigurationFingerprint.Compute(With(new EmitEventAction("receita", ["numero", "serie"]))));
    }

    [Fact]
    public async Task A_unique_emit_on_a_window_that_appears_round_trips_through_the_json_store()
    {
        var store = new JsonConfigurationStore(_directory);
        var configuration = With(new EmitEventAction("receita", ["numero", "serie"]));

        await store.SaveAsync(configuration, CancellationToken.None);
        var loaded = await store.LoadAsync("orcamento", CancellationToken.None);

        var emit = Assert.IsType<EmitEventAction>(loaded.Triggers[0].Actions[1]);
        Assert.Equal(["numero", "serie"], emit.UniqueBy!.Value.ToArray());
        Assert.Equal(TriggerEvents.WindowShown, loaded.Triggers[0].ObservedEvent);
        Assert.Equal(ConfigurationFingerprint.Compute(configuration), ConfigurationFingerprint.Compute(loaded));
    }

    [Fact]
    public async Task A_plain_emit_is_saved_without_unique_fields()
    {
        var store = new JsonConfigurationStore(_directory);

        await store.SaveAsync(With(new EmitEventAction("receita")), CancellationToken.None);
        var loaded = await store.LoadAsync("orcamento", CancellationToken.None);

        Assert.Null(Assert.IsType<EmitEventAction>(loaded.Triggers[0].Actions[1]).UniqueBy);
    }

    private static IntegrationConfiguration With(EmitEventAction emit) => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "orcamento",
        "Orçamento",
        new ApplicationDefinition("FCReceitas.exe", "Orçamentos"),
        [
            new FieldDefinition("numero", "principal", "Número", false, new ElementFingerprint("FCReceitas.exe", "Orçamentos", ClassName: "TwwDBEdit", WindowPath: "TFagronGroupBox[1]/TwwDBEdit[1]")),
            new FieldDefinition("serie", "principal", "Série", false, new ElementFingerprint("FCReceitas.exe", "Orçamentos", ClassName: "TwwDBEdit", WindowPath: "TFagronGroupBox[1]/TwwDBEdit[0]")),
        ],
        [new StageDefinition("principal", "Principal")],
        [new TriggerDefinition("mais_formulas", "principal", new ElementFingerprint("FCReceitas.exe", "Orçamentos", Name: "Orçamento ", ClassName: "TMessageForm"),
            TriggerEvents.WindowShown, [new CaptureFieldsAction(["numero", "serie"]), emit])]);
}
