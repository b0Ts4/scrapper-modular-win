using System.Collections.Immutable;
using Prescriva.Agent.Domain.Events;
using Prescriva.Agent.Infrastructure.Events;
using Prescriva.Agent.Infrastructure.Security;

namespace Prescriva.Agent.Infrastructure.Tests.Events;

public sealed class OutboxCapacityPolicyTests
{
    [Fact]
    public void Below_warning_threshold_reports_normal()
    {
        var policy = new OutboxCapacityPolicy(warningThreshold: 100, criticalThreshold: 200);

        var assessment = policy.Evaluate(50);

        Assert.Equal(OutboxCapacityStatus.Normal, assessment.Status);
        Assert.Null(assessment.Message);
    }

    [Fact]
    public void At_warning_threshold_reports_warning_with_a_message()
    {
        var policy = new OutboxCapacityPolicy(warningThreshold: 100, criticalThreshold: 200);

        var assessment = policy.Evaluate(150);

        Assert.Equal(OutboxCapacityStatus.Warning, assessment.Status);
        Assert.NotNull(assessment.Message);
    }

    [Fact]
    public void At_critical_threshold_reports_critical_with_a_message()
    {
        var policy = new OutboxCapacityPolicy(warningThreshold: 100, criticalThreshold: 200);

        var assessment = policy.Evaluate(250);

        Assert.Equal(OutboxCapacityStatus.Critical, assessment.Status);
        Assert.NotNull(assessment.Message);
    }

    [Fact]
    public async Task Capacity_pressure_alerts_without_deleting_any_pending_event()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prescriva-capacity-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "outbox.db");
        try
        {
            var outbox = new SqliteEventOutbox(dbPath, new DpapiPayloadProtector());
            const int eventCount = 10;
            for (var i = 0; i < eventCount; i++)
            {
                var domainEvent = new DomainEvent(
                    Guid.NewGuid(), "budget-flow", 1, Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "item_captured",
                    new DomainEventPayload(ImmutableDictionary<string, string>.Empty, ImmutableArray<ImmutableDictionary<string, string>>.Empty));
                await outbox.AppendAsync(domainEvent, CancellationToken.None);
            }

            var policy = new OutboxCapacityPolicy(warningThreshold: 5, criticalThreshold: 8);
            var pendingBefore = await outbox.ReadPendingAsync(CancellationToken.None);
            var assessment = policy.Evaluate(pendingBefore.Count);

            Assert.Equal(OutboxCapacityStatus.Critical, assessment.Status);

            var pendingAfter = await outbox.ReadPendingAsync(CancellationToken.None);
            Assert.Equal(eventCount, pendingAfter.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Constructor_rejects_a_critical_threshold_lower_than_the_warning_threshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutboxCapacityPolicy(warningThreshold: 200, criticalThreshold: 100));
    }
}
