using System.Text;
using Microsoft.Data.Sqlite;
using Prescriva.Agent.Application.Jobs;
using Prescriva.Agent.Infrastructure.Jobs;
using Prescriva.Agent.Infrastructure.Security;

namespace Prescriva.Agent.Infrastructure.Tests.Jobs;

/// <summary>The robot's queue survives a restart and keeps requests and results encrypted (plan 22).</summary>
public sealed class SqliteJobStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "prescriva-jobs-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task Jobs_are_kept_in_order_and_survive_a_new_store()
    {
        var store = new SqliteJobStore(_path, new DpapiPayloadProtector());
        await store.AddAsync(RobotJob.Queued("a", RobotJobKind.LaunchQuote, "{\"jobId\":\"a\"}", "1 fórmula(s)"), CancellationToken.None);
        await store.AddAsync(RobotJob.Queued("b", RobotJobKind.LaunchQuote, "{\"jobId\":\"b\"}", "1 fórmula(s)"), CancellationToken.None);

        var reopened = new SqliteJobStore(_path, new DpapiPayloadProtector());
        var next = await reopened.NextQueuedAsync(CancellationToken.None);

        Assert.Equal("a", next!.Id);
        Assert.Equal("{\"jobId\":\"a\"}", next.Request);
        Assert.Equal(["b", "a"], (await reopened.RecentAsync(10, CancellationToken.None)).Select(job => job.Id));
    }

    [Fact]
    public async Task The_same_job_id_is_added_once()
    {
        var store = new SqliteJobStore(_path, new DpapiPayloadProtector());

        var first = await store.AddAsync(RobotJob.Queued("a", RobotJobKind.LaunchQuote, "{}", "x"), CancellationToken.None);
        var second = await store.AddAsync(RobotJob.Queued("a", RobotJobKind.LaunchQuote, "{\"other\":1}", "y"), CancellationToken.None);

        Assert.True(first.Added);
        Assert.False(second.Added);
        Assert.Equal("{}", second.Job.Request);
    }

    [Fact]
    public async Task A_finished_job_is_unreported_until_marked()
    {
        var store = new SqliteJobStore(_path, new DpapiPayloadProtector());
        var (job, _) = await store.AddAsync(RobotJob.Queued("a", RobotJobKind.LaunchQuote, "{}", "x"), CancellationToken.None);

        await store.UpdateAsync(job with { State = RobotJobState.Completed, Result = "{\"state\":\"completed\"}" }, CancellationToken.None);
        var unreported = await store.UnreportedAsync(CancellationToken.None);
        await store.UpdateAsync(unreported[0] with { Reported = true }, CancellationToken.None);

        Assert.Equal("{\"state\":\"completed\"}", unreported[0].Result);
        Assert.Empty(await store.UnreportedAsync(CancellationToken.None));
        Assert.Null(await store.NextQueuedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Requests_and_results_are_not_stored_in_clear_text()
    {
        var store = new SqliteJobStore(_path, new DpapiPayloadProtector());
        var (job, _) = await store.AddAsync(RobotJob.Queued("a", RobotJobKind.LaunchQuote, "{\"secret\":\"PREGABALINA\"}", "x"), CancellationToken.None);
        await store.UpdateAsync(job with { State = RobotJobState.Completed, Result = "{\"numero\":\"RESULTADO899\"}" }, CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var bytes = await File.ReadAllBytesAsync(_path);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("PREGABALINA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RESULTADO899", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_left_running_by_a_crash_is_not_run_again_by_itself()
    {
        // The screen may be half filled: a person decides; the robot does not retype it.
        var store = new SqliteJobStore(_path, new DpapiPayloadProtector());
        var (job, _) = await store.AddAsync(RobotJob.Queued("a", RobotJobKind.LaunchQuote, "{}", "x"), CancellationToken.None);
        await store.UpdateAsync(job with { State = RobotJobState.Running }, CancellationToken.None);

        var reopened = new SqliteJobStore(_path, new DpapiPayloadProtector());

        Assert.Null(await reopened.NextQueuedAsync(CancellationToken.None));
        var after = await reopened.GetAsync("a", CancellationToken.None);
        Assert.Equal(RobotJobState.Stopped, after!.State);
        Assert.Contains("Interrupted", after.Result, StringComparison.Ordinal);
    }
}
