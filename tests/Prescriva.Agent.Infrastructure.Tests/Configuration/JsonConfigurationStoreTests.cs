using System.Collections.Immutable;
using System.Text.Json;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Tests.Configuration;

public sealed class JsonConfigurationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-config-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Action_discriminator_is_specific_to_the_configuration_store()
    {
        var defaultJson = JsonSerializer.Serialize<TriggerActionDefinition>(new CaptureFieldsAction(["item_name"]));

        Assert.DoesNotContain("\"kind\"", defaultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_and_load_preserve_all_action_kinds_and_configuration_values()
    {
        var store = new JsonConfigurationStore(_directory);
        var original = ValidConfiguration();

        await store.SaveAsync(original, CancellationToken.None);
        var loaded = await store.LoadAsync(original.Id, CancellationToken.None);

        Assert.Equivalent(original, loaded, strict: true);
        Assert.Collection(loaded.Triggers[0].Actions,
            action => Assert.IsType<CaptureFieldsAction>(action),
            action => Assert.IsType<EmitEventAction>(action),
            action => Assert.IsType<TransitionStageAction>(action));
        Assert.Collection(loaded.Triggers[1].Actions,
            action => Assert.IsType<ClearStateAction>(action),
            action => Assert.IsType<FinishSessionAction>(action),
            action => Assert.IsType<CancelSessionAction>(action));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, "budget-flow.json")));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("captureFields", document.RootElement.GetProperty("triggers")[0].GetProperty("actions")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Load_rejects_unknown_schema_version_with_stable_code()
    {
        var store = new JsonConfigurationStore(_directory);
        await store.SaveAsync(ValidConfiguration(), CancellationToken.None);
        var path = Path.Combine(_directory, "budget-flow.json");
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<ConfigurationValidationException>(() => store.LoadAsync("budget-flow", CancellationToken.None));
        Assert.Contains(error.Errors, item => item.Code == "UNSUPPORTED_SCHEMA_VERSION");
    }

    [Fact]
    public async Task Load_rejects_malformed_json_with_stable_code()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "budget-flow.json"), "{ bad json");

        var error = await Assert.ThrowsAsync<ConfigurationValidationException>(() =>
            new JsonConfigurationStore(_directory).LoadAsync("budget-flow", CancellationToken.None));

        Assert.Contains(error.Errors, item => item.Code == "MALFORMED_JSON");
    }

    [Fact]
    public async Task Invalid_save_does_not_overwrite_existing_configuration()
    {
        var store = new JsonConfigurationStore(_directory);
        await store.SaveAsync(ValidConfiguration(), CancellationToken.None);
        var path = Path.Combine(_directory, "budget-flow.json");
        var before = await File.ReadAllTextAsync(path);

        var error = await Assert.ThrowsAsync<ConfigurationValidationException>(() =>
            store.SaveAsync(ValidConfiguration() with { SchemaVersion = 2 }, CancellationToken.None));

        Assert.Contains(error.Errors, item => item.Code == "UNSUPPORTED_SCHEMA_VERSION");
        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Successful_overwrite_replaces_the_existing_file_content()
    {
        var store = new JsonConfigurationStore(_directory);
        await store.SaveAsync(ValidConfiguration(), CancellationToken.None);
        var path = Path.Combine(_directory, "budget-flow.json");
        var before = await File.ReadAllTextAsync(path);

        var updated = ValidConfiguration() with { Name = "Budget flow (renamed)" };
        await store.SaveAsync(updated, CancellationToken.None);

        var after = await File.ReadAllTextAsync(path);
        Assert.NotEqual(before, after);
        Assert.Contains("Budget flow (renamed)", after, StringComparison.Ordinal);

        var loaded = await store.LoadAsync(updated.Id, CancellationToken.None);
        Assert.Equivalent(updated, loaded, strict: true);
    }

    [Fact]
    public async Task Default_and_explicit_empty_selector_arrays_round_trip_equivalently()
    {
        var store = new JsonConfigurationStore(_directory);
        var defaultSelector = new ElementFingerprint("erp.exe", "Budget", AutomationId: "item-name", ControlType: "Edit");
        var explicitEmptySelector = defaultSelector with
        {
            Ancestors = ImmutableArray<AncestorFingerprint>.Empty,
            NearbyLabels = ImmutableArray<string>.Empty
        };

        var withDefault = ValidConfiguration() with
        {
            Id = "default-selector-flow",
            Fields = [new FieldDefinition("item_name", "entry", "Item name", true, defaultSelector)]
        };
        var withExplicitEmpty = ValidConfiguration() with
        {
            Id = "explicit-empty-selector-flow",
            Fields = [new FieldDefinition("item_name", "entry", "Item name", true, explicitEmptySelector)]
        };

        await store.SaveAsync(withDefault, CancellationToken.None);
        await store.SaveAsync(withExplicitEmpty, CancellationToken.None);

        var loadedDefault = await store.LoadAsync(withDefault.Id, CancellationToken.None);
        var loadedExplicitEmpty = await store.LoadAsync(withExplicitEmpty.Id, CancellationToken.None);

        var defaultLoadedSelector = loadedDefault.Fields[0].Selector;
        var explicitEmptyLoadedSelector = loadedExplicitEmpty.Fields[0].Selector;

        Assert.True(defaultLoadedSelector.Ancestors.IsDefaultOrEmpty);
        Assert.True(defaultLoadedSelector.NearbyLabels.IsDefaultOrEmpty);
        Assert.True(explicitEmptyLoadedSelector.Ancestors.IsDefaultOrEmpty);
        Assert.True(explicitEmptyLoadedSelector.NearbyLabels.IsDefaultOrEmpty);

        var candidate = new ElementCandidate("target", defaultSelector with { AutomationId = "item-name", ControlType = "Edit" });
        var weights = new SelectorWeights();
        var matcher = new SelectorMatcher();

        var defaultMatch = matcher.Match(defaultLoadedSelector, [candidate], weights);
        var explicitEmptyMatch = matcher.Match(explicitEmptyLoadedSelector, [candidate], weights);

        Assert.Equal(defaultMatch.Status, explicitEmptyMatch.Status);
        Assert.Equal(defaultMatch.Score, explicitEmptyMatch.Score);
    }

    [Fact]
    public async Task Load_rejects_unknown_action_kind()
    {
        var store = new JsonConfigurationStore(_directory);
        await store.SaveAsync(ValidConfiguration(), CancellationToken.None);
        var path = Path.Combine(_directory, "budget-flow.json");
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("\"kind\": \"captureFields\"", "\"kind\": \"unknownAction\"", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<ConfigurationValidationException>(() => store.LoadAsync("budget-flow", CancellationToken.None));
        Assert.Contains(error.Errors, item => item.Code == "MALFORMED_JSON");
    }

    [Fact]
    public async Task Load_rejects_action_without_a_kind()
    {
        var store = new JsonConfigurationStore(_directory);
        await store.SaveAsync(ValidConfiguration(), CancellationToken.None);
        var path = Path.Combine(_directory, "budget-flow.json");
        var json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace("\"kind\": \"captureFields\",", "", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<ConfigurationValidationException>(() => store.LoadAsync("budget-flow", CancellationToken.None));
        Assert.Contains(error.Errors, item => item.Code == "MALFORMED_JSON");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static IntegrationConfiguration ValidConfiguration()
    {
        var fieldSelector = new ElementFingerprint("erp.exe", "Budget", AutomationId: "item-name", ControlType: "Edit",
            Ancestors: [], NearbyLabels: []);
        var triggerSelector = new ElementFingerprint("erp.exe", "Budget", AutomationId: "add", ControlType: "Button",
            Ancestors: [], NearbyLabels: []);

        return new IntegrationConfiguration(
            IntegrationConfiguration.CurrentSchemaVersion,
            "budget-flow",
            "Budget flow",
            new ApplicationDefinition("erp.exe", "Budget"),
            [new FieldDefinition("item_name", "entry", "Item name", true, fieldSelector)],
            [new StageDefinition("entry", "Entry"), new StageDefinition("review", "Review")],
            [
                new TriggerDefinition("add", "entry", triggerSelector, "Invoked",
                    [new CaptureFieldsAction(["item_name"]), new EmitEventAction("item_added"), new TransitionStageAction("review")]),
                new TriggerDefinition("finish", "review", triggerSelector with { AutomationId = "finish" }, "Invoked",
                    [new ClearStateAction(), new FinishSessionAction(), new CancelSessionAction()])
            ]);
    }
}
