using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Application.Runtime;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Tests.Runtime;

/// <summary>
/// What the Agent monitors when it starts with Windows: only the integration the operator
/// left active, and only while its approval still matches its content. Anything else is
/// skipped with a visible reason - never activated.
/// </summary>
public sealed class StartupActivationTests
{
    private static readonly DateTimeOffset ApprovedAt = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_integration_left_active_is_resumed_when_its_approval_still_matches()
    {
        var configuration = Configuration("budget");
        var (activation, _) = Create(activeId: "budget", configurations: [configuration], approvals: [Approve(configuration)]);

        var decision = await activation.DecideAsync(CancellationToken.None);

        Assert.Equal(StartupDecisionKind.Activate, decision.Kind);
        Assert.Equal(configuration, decision.Configuration);
        Assert.True(decision.Approval!.IsValidFor(ConfigurationFingerprint.Compute(configuration)));
    }

    [Fact]
    public async Task Nothing_is_activated_when_the_operator_left_no_integration_active()
    {
        var configuration = Configuration("budget");
        var (activation, _) = Create(activeId: null, configurations: [configuration], approvals: [Approve(configuration)]);

        var decision = await activation.DecideAsync(CancellationToken.None);

        Assert.Equal(StartupDecisionKind.NothingActive, decision.Kind);
        Assert.Null(decision.Configuration);
    }

    [Fact]
    public async Task An_integration_never_tested_is_not_activated()
    {
        var (activation, _) = Create(activeId: "budget", configurations: [Configuration("budget")], approvals: []);

        var decision = await activation.DecideAsync(CancellationToken.None);

        Assert.Equal(StartupDecisionKind.NotApproved, decision.Kind);
        Assert.Equal("budget", decision.ConfigurationId);
        Assert.Null(decision.Configuration);
    }

    [Fact]
    public async Task An_integration_edited_after_its_approval_is_not_activated()
    {
        var approved = Configuration("budget");
        var edited = approved with { Name = "Orçamento (editado)" };
        var (activation, _) = Create(activeId: "budget", configurations: [edited], approvals: [Approve(approved)]);

        var decision = await activation.DecideAsync(CancellationToken.None);

        Assert.Equal(StartupDecisionKind.ChangedSinceTest, decision.Kind);
        Assert.Null(decision.Configuration);
    }

    [Fact]
    public async Task An_integration_that_can_no_longer_be_loaded_is_not_activated()
    {
        var (activation, _) = Create(activeId: "deleted", configurations: [], approvals: []);

        var decision = await activation.DecideAsync(CancellationToken.None);

        Assert.Equal(StartupDecisionKind.ConfigurationUnavailable, decision.Kind);
        Assert.Equal("deleted", decision.ConfigurationId);
    }

    [Fact]
    public void Each_decision_has_a_visible_portuguese_description()
    {
        foreach (var kind in Enum.GetValues<StartupDecisionKind>())
        {
            var text = new StartupDecision(kind, "budget").Describe();
            Assert.False(string.IsNullOrWhiteSpace(text));
        }

        Assert.Contains("budget", new StartupDecision(StartupDecisionKind.ChangedSinceTest, "budget").Describe(), StringComparison.Ordinal);
    }

    private static (StartupActivation Activation, InMemoryPreferences Preferences) Create(
        string? activeId,
        IntegrationConfiguration[] configurations,
        ConfigurationApproval[] approvals)
    {
        var preferences = new InMemoryPreferences { ActiveConfigurationId = activeId };
        var activation = new StartupActivation(
            preferences,
            new InMemoryConfigurations(configurations),
            new ApprovalService(new InMemoryApprovals(approvals)));
        return (activation, preferences);
    }

    private static ConfigurationApproval Approve(IntegrationConfiguration configuration) =>
        new(configuration.Id, ConfigurationFingerprint.Compute(configuration), ApprovedAt);

    private static IntegrationConfiguration Configuration(string id) => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        id,
        "Orçamento",
        new ApplicationDefinition("Prescriva.Agent.TestTarget.exe", "Prescriva Agent Test Target"),
        [new FieldDefinition("medication", "budget", "Medicamento", Required: true, Selector: Selector("MedicationTextBox"))],
        [new StageDefinition("budget", "Orçamento")],
        [new TriggerDefinition("add", "budget", Selector("AddButton"), "Invoke", [new CaptureFieldsAction(["medication"]), new EmitEventAction("item_added")])]);

    private static ElementFingerprint Selector(string automationId) =>
        new("Prescriva.Agent.TestTarget.exe", "Prescriva Agent Test Target", AutomationId: automationId);

    private sealed class InMemoryPreferences : IMonitoringPreferenceStore
    {
        public string? ActiveConfigurationId { get; set; }

        public Task<string?> GetActiveConfigurationIdAsync(CancellationToken cancellationToken) => Task.FromResult(ActiveConfigurationId);

        public Task SetActiveConfigurationIdAsync(string? configurationId, CancellationToken cancellationToken)
        {
            ActiveConfigurationId = configurationId;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryConfigurations(IntegrationConfiguration[] configurations) : IConfigurationStore
    {
        public Task<IntegrationConfiguration> LoadAsync(string id, CancellationToken cancellationToken) =>
            configurations.FirstOrDefault(c => c.Id == id) is { } found
                ? Task.FromResult(found)
                : Task.FromException<IntegrationConfiguration>(new FileNotFoundException("missing", id + ".json"));

        public Task SaveAsync(IntegrationConfiguration configuration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryApprovals(ConfigurationApproval[] approvals) : IApprovalStore
    {
        public Task SaveAsync(ConfigurationApproval approval, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ConfigurationApproval?> LoadAsync(string configurationId, CancellationToken cancellationToken) =>
            Task.FromResult(approvals.FirstOrDefault(a => a.ConfigurationId == configurationId));

        public Task DeleteAsync(string configurationId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
