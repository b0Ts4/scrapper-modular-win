using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Tests.Configuration;

/// <summary>
/// File fields (plan 6) extend the configuration model without disturbing existing
/// text-only configurations: they still load, and their content hash - which approvals
/// are bound to - does not change.
/// </summary>
public sealed class FieldKindTests : IDisposable
{
    // Characterization: the hash of TextOnly() computed by the code before field kinds existed.
    private const string TextOnlyFingerprintBeforeFieldKinds = "746DDFCDB43694B0340C056458664A7BD3C28A0906206AE3C99DF73B069450F7";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-field-kind-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void A_text_only_configuration_keeps_the_content_hash_its_approvals_are_bound_to()
    {
        Assert.Equal(TextOnlyFingerprintBeforeFieldKinds, ConfigurationFingerprint.Compute(TextOnly()));
    }

    [Fact]
    public void Fields_are_text_unless_declared_otherwise()
    {
        Assert.Equal(FieldKind.Text, TextOnly().Fields[0].Kind);
    }

    [Fact]
    public void Turning_a_field_into_a_file_field_changes_the_content_hash()
    {
        var text = TextOnly();
        var file = text with { Fields = [text.Fields[0] with { Kind = FieldKind.File }] };

        Assert.NotEqual(ConfigurationFingerprint.Compute(text), ConfigurationFingerprint.Compute(file));
    }

    [Fact]
    public async Task A_screen_image_field_round_trips_through_the_json_store_as_a_readable_kind()
    {
        var store = new JsonConfigurationStore(_directory);
        var text = TextOnly();
        var image = text with { Fields = [text.Fields[0] with { Kind = FieldKind.ScreenImage }] };

        await store.SaveAsync(image, CancellationToken.None);
        var json = await File.ReadAllTextAsync(Path.Combine(_directory, "budget-flow.json"));
        var loaded = await store.LoadAsync("budget-flow", CancellationToken.None);

        Assert.Contains("\"kind\": \"screenImage\"", json, StringComparison.Ordinal);
        Assert.Equal(FieldKind.ScreenImage, loaded.Fields[0].Kind);
    }

    [Fact]
    public async Task A_file_field_round_trips_through_the_json_store_as_a_readable_kind()
    {
        var store = new JsonConfigurationStore(_directory);
        var text = TextOnly();
        var file = text with { Fields = [text.Fields[0] with { Kind = FieldKind.File }] };

        await store.SaveAsync(file, CancellationToken.None);
        var json = await File.ReadAllTextAsync(Path.Combine(_directory, "budget-flow.json"));
        var loaded = await store.LoadAsync("budget-flow", CancellationToken.None);

        Assert.Contains("\"kind\": \"file\"", json, StringComparison.Ordinal);
        Assert.Equal(FieldKind.File, loaded.Fields[0].Kind);
        Assert.Equal(ConfigurationFingerprint.Compute(file), ConfigurationFingerprint.Compute(loaded));
    }

    [Fact]
    public async Task A_configuration_saved_before_field_kinds_existed_loads_with_text_fields()
    {
        var store = new JsonConfigurationStore(_directory);
        await store.SaveAsync(TextOnly(), CancellationToken.None);
        var path = Path.Combine(_directory, "budget-flow.json");
        var json = await File.ReadAllTextAsync(path);
        var withoutKind = System.Text.RegularExpressions.Regex.Replace(json, ",\\s*\"kind\": \"text\"", string.Empty);
        Assert.DoesNotContain("\"text\"", withoutKind, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, withoutKind);

        var loaded = await store.LoadAsync("budget-flow", CancellationToken.None);

        Assert.Equal(FieldKind.Text, loaded.Fields[0].Kind);
        Assert.Equal(TextOnlyFingerprintBeforeFieldKinds, ConfigurationFingerprint.Compute(loaded));
    }

    [Fact]
    public void Turning_a_field_into_an_OCR_field_changes_the_content_hash()
    {
        var text = TextOnly();
        var ocr = text with { Fields = [text.Fields[0] with { Kind = FieldKind.OcrText }] };
        var file = text with { Fields = [text.Fields[0] with { Kind = FieldKind.File }] };

        Assert.NotEqual(ConfigurationFingerprint.Compute(text), ConfigurationFingerprint.Compute(ocr));
        Assert.NotEqual(ConfigurationFingerprint.Compute(file), ConfigurationFingerprint.Compute(ocr));
    }

    [Fact]
    public async Task An_OCR_field_round_trips_through_the_json_store_as_a_readable_kind()
    {
        var store = new JsonConfigurationStore(_directory);
        var text = TextOnly();
        var ocr = text with { Fields = [text.Fields[0] with { Kind = FieldKind.OcrText }] };

        await store.SaveAsync(ocr, CancellationToken.None);
        var json = await File.ReadAllTextAsync(Path.Combine(_directory, "budget-flow.json"));
        var loaded = await store.LoadAsync("budget-flow", CancellationToken.None);

        Assert.Contains("\"kind\": \"ocrText\"", json, StringComparison.Ordinal);
        Assert.Equal(FieldKind.OcrText, loaded.Fields[0].Kind);
    }

    private static IntegrationConfiguration TextOnly() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "budget-flow",
        "Budget flow",
        new ApplicationDefinition("erp.exe", "Budget"),
        [new FieldDefinition("item_name", "entry", "Item name", true, new ElementFingerprint("erp.exe", "Budget", AutomationId: "item-name", ControlType: "Edit"))],
        [new StageDefinition("entry", "Entry")],
        [new TriggerDefinition("add", "entry", new ElementFingerprint("erp.exe", "Budget", AutomationId: "add", ControlType: "Button"), "Invoke",
            [new CaptureFieldsAction(["item_name"]), new EmitEventAction("item_added")])]);
}
