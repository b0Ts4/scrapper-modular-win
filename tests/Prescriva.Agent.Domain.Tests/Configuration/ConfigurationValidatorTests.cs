using System.Collections.Immutable;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Domain.Tests.Configuration;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void Valid_two_stage_integration_is_accepted()
    {
        Assert.True(ConfigurationValidator.Validate(ValidConfiguration()).IsValid);
    }

    [Theory]
    [InlineData("field", "DUPLICATE_FIELD_ID")]
    [InlineData("stage", "DUPLICATE_STAGE_ID")]
    [InlineData("trigger", "DUPLICATE_TRIGGER_ID")]
    public void Duplicate_semantic_ids_are_rejected(string kind, string expectedCode)
    {
        var configuration = ValidConfiguration();
        configuration = kind switch
        {
            "field" => configuration with { Fields = configuration.Fields.Add(configuration.Fields[0]) },
            "stage" => configuration with { Stages = configuration.Stages.Add(configuration.Stages[0]) },
            _ => configuration with { Triggers = configuration.Triggers.Add(configuration.Triggers[0]) }
        };

        Assert.Contains(ConfigurationValidator.Validate(configuration).Errors, error => error.Code == expectedCode);
    }

    [Fact]
    public void Capture_action_cannot_reference_an_unknown_field()
    {
        var configuration = ValidConfiguration();
        var trigger = configuration.Triggers[0] with
        {
            Actions = [new CaptureFieldsAction(["unknown"])]
        };

        Assert.Contains(ConfigurationValidator.Validate(configuration with { Triggers = [trigger, configuration.Triggers[1]] }).Errors,
            error => error.Code == "UNKNOWN_FIELD_REFERENCE");
    }

    [Fact]
    public void Transition_action_cannot_reference_an_unknown_stage()
    {
        var configuration = ValidConfiguration();
        var trigger = configuration.Triggers[0] with
        {
            Actions = [new TransitionStageAction("unknown")]
        };

        Assert.Contains(ConfigurationValidator.Validate(configuration with { Triggers = [trigger, configuration.Triggers[1]] }).Errors,
            error => error.Code == "UNKNOWN_STAGE_REFERENCE");
    }

    [Fact]
    public void Unknown_schema_version_is_rejected()
    {
        Assert.Contains(ConfigurationValidator.Validate(ValidConfiguration() with { SchemaVersion = 2 }).Errors,
            error => error.Code == "UNSUPPORTED_SCHEMA_VERSION");
    }

    [Fact]
    public void Stage_without_a_name_cannot_be_activated()
    {
        var configuration = ValidConfiguration();
        var unnamedStage = configuration.Stages[0] with { Name = " " };

        Assert.Contains(ConfigurationValidator.Validate(configuration with { Stages = [unnamedStage, configuration.Stages[1]] }).Errors,
            error => error.Code == "INVALID_STAGE_NAME");
    }

    internal static IntegrationConfiguration ValidConfiguration()
    {
        var fieldSelector = new ElementFingerprint("erp.exe", "Budget", AutomationId: "item-name", ControlType: "Edit");
        var triggerSelector = new ElementFingerprint("erp.exe", "Budget", AutomationId: "add", ControlType: "Button");

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
