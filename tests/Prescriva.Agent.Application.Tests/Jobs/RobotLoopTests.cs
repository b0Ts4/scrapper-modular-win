using System.Collections.Immutable;
using Prescriva.Agent.Application.Jobs;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Application.Tests.Jobs;

/// <summary>One round of the robot (plan 22): fetch jobs, run every queued one, send the results back.</summary>
public sealed class RobotLoopTests
{
    [Fact]
    public async Task A_round_pulls_runs_every_queued_job_and_pushes_the_results()
    {
        var store = new JobProcessorTests.MemoryJobStore();
        var connection = new FakeConnection(store, jobsToHand: 2);
        var started = new List<string>();
        var loop = new RobotLoop(Processor(store), connection);
        loop.JobStarted += job => started.Add(job.Id);

        await loop.RunRoundAsync(CancellationToken.None);

        Assert.Equal(["job-0", "job-1"], started);
        Assert.Equal(2, connection.Pushed);
        Assert.All(store.Jobs(), job => Assert.Equal(RobotJobState.Completed, job.State));
    }

    [Fact]
    public async Task Without_a_CRM_the_local_queue_still_runs()
    {
        var store = new JobProcessorTests.MemoryJobStore();
        await store.AddAsync(RobotJob.Queued("local-1", RobotJobKind.LaunchQuote, JobJson.Write(QuoteRunsTests.Request(1) with { JobId = "local-1" }), "1"), CancellationToken.None);

        await new RobotLoop(Processor(store), connection: null).RunRoundAsync(CancellationToken.None);

        Assert.Equal(RobotJobState.Completed, (await store.GetAsync("local-1", CancellationToken.None))!.State);
    }

    [Fact]
    public async Task The_CRM_being_unreachable_is_reported_and_the_local_queue_still_runs()
    {
        var store = new JobProcessorTests.MemoryJobStore();
        await store.AddAsync(RobotJob.Queued("local-1", RobotJobKind.LaunchQuote, JobJson.Write(QuoteRunsTests.Request(1) with { JobId = "local-1" }), "1"), CancellationToken.None);
        var loop = new RobotLoop(Processor(store), new FakeConnection(store, 0) { Fails = true });
        string? problem = null;
        loop.ConnectionProblem += message => problem = message;

        await loop.RunRoundAsync(CancellationToken.None);

        Assert.NotNull(problem);
        Assert.Equal(RobotJobState.Completed, (await store.GetAsync("local-1", CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Stopping_ends_the_round_after_the_current_job()
    {
        var store = new JobProcessorTests.MemoryJobStore();
        using var stop = new CancellationTokenSource();
        var loop = new RobotLoop(Processor(store, () => stop.Cancel()), new FakeConnection(store, jobsToHand: 2));

        await loop.RunRoundAsync(stop.Token);

        Assert.Equal(RobotJobState.Cancelled, (await store.GetAsync("job-0", CancellationToken.None))!.State);
        Assert.Equal(RobotJobState.Queued, (await store.GetAsync("job-1", CancellationToken.None))!.State);
    }

    private static JobProcessor Processor(IJobStore store, Action? whileRunning = null) => new(
        store,
        _ => Task.FromResult(new Playbook("lancar", "Lançar", "fcreceitas", "Orçamentos", [new PlaybookStep("v", PlaybookStepKind.SetField, "A[0]", "{volume}")], [])),
        new JobProcessorTests.MemoryPolicyStore(),
        () => new RobotSettings("19", RobotLevel.Saves),
        (_, _, token) =>
        {
            whileRunning?.Invoke();
            return Task.FromResult(token.IsCancellationRequested
                ? new PlaybookRunResult(PlaybookRunStatus.Stopped, "v", PlaybookStopReason.Cancelled)
                : new PlaybookRunResult(PlaybookRunStatus.Completed, Captured: ImmutableDictionary<string, string>.Empty.Add("numero_orcamento", "1")));
        });

    private sealed class FakeConnection(JobProcessorTests.MemoryJobStore store, int jobsToHand) : IJobConnection
    {
        public bool Fails { get; init; }

        public int Pushed { get; private set; }

        public async Task<int> PullAsync(CancellationToken cancellationToken)
        {
            if (Fails)
            {
                throw new HttpRequestException("unreachable");
            }

            for (var index = 0; index < jobsToHand; index++)
            {
                var id = $"job-{index}";
                await store.AddAsync(RobotJob.Queued(id, RobotJobKind.LaunchQuote, JobJson.Write(QuoteRunsTests.Request(1) with { JobId = id }), "1"), cancellationToken);
            }

            return jobsToHand;
        }

        public async Task<int> PushResultsAsync(CancellationToken cancellationToken)
        {
            if (Fails)
            {
                throw new HttpRequestException("unreachable");
            }

            var unreported = await store.UnreportedAsync(cancellationToken);
            foreach (var job in unreported)
            {
                await store.UpdateAsync(job with { Reported = true }, cancellationToken);
            }

            Pushed += unreported.Count;
            return unreported.Count;
        }
    }
}
