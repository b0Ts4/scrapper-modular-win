using System.Collections.Concurrent;
using System.Collections.Immutable;
using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Application.Testing;
using Prescriva.Agent.Domain.Configuration;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Application.Tests.Testing;

/// <summary>
/// ApprovalService answers one question for the activation gate - is there an approval for
/// exactly this content? - across Agent restarts (a new service over the same store).
/// </summary>
public sealed class ApprovalServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_configuration_never_tested_is_NotTested()
    {
        var status = await new ApprovalService(new InMemoryApprovalStore()).GetStatusAsync(Configuration());

        Assert.Equal(ApprovalState.NotTested, status.State);
        Assert.Null(status.Approval);
    }

    [Fact]
    public async Task A_recorded_passing_report_approves_the_same_content_after_a_restart()
    {
        var store = new InMemoryApprovalStore();
        var configuration = Configuration();
        var recorded = await new ApprovalService(store, () => Now).RecordAsync(PassingReport(configuration));

        var status = await new ApprovalService(store).GetStatusAsync(configuration);

        Assert.Equal(ApprovalState.Approved, status.State);
        Assert.Equal(recorded, status.Approval);
        Assert.Equal(Now, status.Approval!.ApprovedAtUtc);
    }

    [Fact]
    public async Task Any_edit_makes_the_stored_approval_unusable_and_reverting_restores_it()
    {
        var store = new InMemoryApprovalStore();
        var configuration = Configuration();
        var service = new ApprovalService(store, () => Now);
        await service.RecordAsync(PassingReport(configuration));

        var edited = configuration with
        {
            Fields = [configuration.Fields[0] with { Required = false }],
        };
        var changed = await service.GetStatusAsync(edited);
        Assert.Equal(ApprovalState.ChangedSinceTest, changed.State);
        Assert.Null(changed.Approval);
        Assert.Equal(Now, changed.LastApprovedAtUtc);

        var reverted = edited with { Fields = configuration.Fields };
        Assert.Equal(ApprovalState.Approved, (await service.GetStatusAsync(reverted)).State);
    }

    [Fact]
    public async Task A_failed_report_cannot_be_recorded_and_leaves_the_previous_approval_untouched()
    {
        var store = new InMemoryApprovalStore();
        var configuration = Configuration();
        var service = new ApprovalService(store, () => Now);
        await service.RecordAsync(PassingReport(configuration));

        var failed = PassingReport(configuration) with
        {
            FieldResults = [new FieldCheckResult("medication", FieldCheckOutcome.NotFound, null, 0, FieldCheckResult.FieldNotFoundCode)],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync(failed));
        Assert.Equal(ApprovalState.Approved, (await service.GetStatusAsync(configuration)).State);
    }

    private static IntegrationTestReport PassingReport(IntegrationConfiguration configuration) => new(
        configuration.Id,
        ConfigurationFingerprint.Compute(configuration),
        Now,
        [new FieldCheckResult("medication", FieldCheckOutcome.Found, "uia", 1.0, null)],
        [new TriggerCheckResult("add_item", TriggerCheckOutcome.Detected, ImmutableArray<string>.Empty, ["item_added"], null)]);

    private static IntegrationConfiguration Configuration() => new(
        IntegrationConfiguration.CurrentSchemaVersion,
        "approval-config",
        "Approval configuration",
        new ApplicationDefinition("Fake.App", "Fake Window"),
        [new FieldDefinition("medication", "budget", "Medicamento", Required: true, Selector: new ElementFingerprint("Fake.App", "Fake Window", AutomationId: "Medication"))],
        [new StageDefinition("budget", "Orçamento")],
        [new TriggerDefinition("add_item", "budget", new ElementFingerprint("Fake.App", "Fake Window", AutomationId: "Add"), "Invoke",
            [new CaptureFieldsAction(["medication"]), new EmitEventAction("item_added")])]);

    private sealed class InMemoryApprovalStore : IApprovalStore
    {
        private readonly ConcurrentDictionary<string, ConfigurationApproval> _approvals = new();

        public Task SaveAsync(ConfigurationApproval approval, CancellationToken cancellationToken)
        {
            _approvals[approval.ConfigurationId] = approval;
            return Task.CompletedTask;
        }

        public Task<ConfigurationApproval?> LoadAsync(string configurationId, CancellationToken cancellationToken) =>
            Task.FromResult(_approvals.TryGetValue(configurationId, out var approval) ? approval : null);

        public Task DeleteAsync(string configurationId, CancellationToken cancellationToken)
        {
            _approvals.TryRemove(configurationId, out _);
            return Task.CompletedTask;
        }
    }
}
