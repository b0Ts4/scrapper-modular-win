using System.Collections.Immutable;
using System.Text.Json;
using Prescriva.Agent.Application.Jobs;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Application.Tests.Jobs;

/// <summary>The robot takes the next queued job, runs its formulas and keeps the result (plan 22).</summary>
public sealed class JobProcessorTests
{
    [Fact]
    public async Task A_job_runs_every_formula_and_keeps_what_was_read_back()
    {
        var store = new MemoryJobStore();
        await store.AddAsync(Job("job-1", QuoteRunsTests.Request(2)), CancellationToken.None);
        var runs = new List<PlaybookData>();
        var processor = Processor(store, (playbook, data, _) =>
        {
            runs.Add(data);
            return Task.FromResult(new PlaybookRunResult(PlaybookRunStatus.Completed,
                Captured: ImmutableDictionary<string, string>.Empty.Add("numero_orcamento", "899200").Add("serie", (runs.Count - 1).ToString())));
        });

        var processed = await processor.ProcessNextAsync(CancellationToken.None);

        Assert.Equal("job-1", processed!.Id);
        Assert.Equal(2, runs.Count);
        var job = await store.GetAsync("job-1", CancellationToken.None);
        Assert.Equal(RobotJobState.Completed, job!.State);
        var result = JsonDocument.Parse(job.Result!).RootElement;
        Assert.Equal("completed", result.GetProperty("state").GetString());
        Assert.Equal("899200", result.GetProperty("formulas")[1].GetProperty("numero_orcamento").GetString());
        Assert.Equal("1", result.GetProperty("formulas")[1].GetProperty("serie").GetString());
    }

    [Fact]
    public async Task A_stopped_formula_stops_the_job_says_where_and_remembers_the_notice()
    {
        var store = new MemoryJobStore();
        var policies = new MemoryPolicyStore();
        await store.AddAsync(Job("job-1", QuoteRunsTests.Request(2)), CancellationToken.None);
        var calls = 0;
        var processor = Processor(store, (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new PlaybookRunResult(PlaybookRunStatus.Stopped, "componentes[1]", PlaybookStopReason.UnknownDialog, "TMessageForm 'Atenção!'",
                Unknown: new UnknownDialog("TMessageForm", "Atenção!", ["OK"], "Interação medicamentosa")));
        }, policies);

        await processor.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(1, calls); // the second formula is not started
        var job = await store.GetAsync("job-1", CancellationToken.None);
        Assert.Equal(RobotJobState.Stopped, job!.State);
        var result = JsonDocument.Parse(job.Result!).RootElement;
        Assert.Equal("UnknownDialog", result.GetProperty("stop").GetProperty("reason").GetString());
        Assert.Equal(0, result.GetProperty("stop").GetProperty("formula").GetInt32());
        Assert.Equal("Interação medicamentosa", result.GetProperty("unknownNotice").GetProperty("text").GetString());
        Assert.Single(policies.Seen);
    }

    [Fact]
    public async Task The_pharmacy_notice_choices_are_used()
    {
        var store = new MemoryJobStore();
        var policies = new MemoryPolicyStore { Policy = new DialogPolicy("lancar", [new DialogRule("aviso_1", "TMessageForm", "Atenção", null, DialogResponse.Press, "OK")]) };
        await store.AddAsync(Job("job-1", QuoteRunsTests.Request(1)), CancellationToken.None);
        Playbook? used = null;
        var processor = Processor(store, (playbook, _, _) =>
        {
            used = playbook;
            return Task.FromResult(new PlaybookRunResult(PlaybookRunStatus.Completed));
        }, policies);

        await processor.ProcessNextAsync(CancellationToken.None);

        Assert.Equal("aviso_1", used!.Dialogs[0].Id);
    }

    [Fact]
    public async Task A_request_that_cannot_be_used_stops_the_job_without_running()
    {
        var store = new MemoryJobStore();
        await store.AddAsync(new RobotJob("job-1", RobotJobKind.LaunchQuote, RobotJobState.Queued, "{ broken", null, DateTimeOffset.Now, DateTimeOffset.Now, "?"), CancellationToken.None);
        var ran = false;
        var processor = Processor(store, (_, _, _) =>
        {
            ran = true;
            return Task.FromResult(new PlaybookRunResult(PlaybookRunStatus.Completed));
        });

        await processor.ProcessNextAsync(CancellationToken.None);

        Assert.False(ran);
        Assert.Equal(RobotJobState.Stopped, (await store.GetAsync("job-1", CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Stopping_the_robot_cancels_the_running_job()
    {
        var store = new MemoryJobStore();
        await store.AddAsync(Job("job-1", QuoteRunsTests.Request(1)), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var processor = Processor(store, (_, _, token) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new PlaybookRunResult(PlaybookRunStatus.Stopped, "volume", PlaybookStopReason.Cancelled));
        });

        await processor.ProcessNextAsync(cancellation.Token);

        Assert.Equal(RobotJobState.Cancelled, (await store.GetAsync("job-1", CancellationToken.None))!.State);
    }

    [Fact]
    public async Task Nothing_queued_means_nothing_to_do()
    {
        var processor = Processor(new MemoryJobStore(), (_, _, _) => throw new InvalidOperationException());

        Assert.Null(await processor.ProcessNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_same_job_sent_twice_is_queued_once()
    {
        var store = new MemoryJobStore();

        var first = await store.AddAsync(Job("job-1", QuoteRunsTests.Request(1)), CancellationToken.None);
        var second = await store.AddAsync(Job("job-1", QuoteRunsTests.Request(2)), CancellationToken.None);

        Assert.True(first.Added);
        Assert.False(second.Added);
        Assert.Single(await store.RecentAsync(10, CancellationToken.None));
    }

    private static RobotJob Job(string id, LaunchQuoteRequest request) =>
        RobotJob.Queued(id, RobotJobKind.LaunchQuote, JobJson.Write(request with { JobId = id }), $"{request.Formulas.Length} fórmula(s)");

    private static JobProcessor Processor(IJobStore store, Func<Playbook, PlaybookData, CancellationToken, Task<PlaybookRunResult>> run, MemoryPolicyStore? policies = null) =>
        new(store, _ => Task.FromResult(Playbook()), policies ?? new MemoryPolicyStore(), () => new RobotSettings("19", RobotLevel.Saves), run);

    private static Playbook Playbook() => new(
        "lancar", "Lançar", "fcreceitas", "Orçamentos",
        [new PlaybookStep("volume", PlaybookStepKind.SetField, "TPanel[0]/TEdit[0]", "{volume}")],
        [new DialogRule("estoque", "TMessageForm", "Atenção", "Estoque", DialogResponse.Press, "OK")]);

    internal sealed class MemoryPolicyStore : IDialogPolicyStore
    {
        public DialogPolicy? Policy { get; set; }

        public List<UnknownDialog> Seen { get; } = [];

        public Task<DialogPolicy?> LoadAsync(string playbookId, CancellationToken cancellationToken) => Task.FromResult(Policy);

        public Task SaveAsync(DialogPolicy policy, CancellationToken cancellationToken)
        {
            Policy = policy;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SeenNotice>> SeenAsync(string playbookId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SeenNotice>>([]);

        public Task RecordSeenAsync(string playbookId, UnknownDialog dialog, CancellationToken cancellationToken)
        {
            Seen.Add(dialog);
            return Task.CompletedTask;
        }
    }

    internal sealed class MemoryJobStore : IJobStore
    {
        private readonly List<RobotJob> _jobs = [];

        public IReadOnlyList<RobotJob> Jobs() => _jobs.ToArray();

        public Task<(RobotJob Job, bool Added)> AddAsync(RobotJob job, CancellationToken cancellationToken)
        {
            var existing = _jobs.FirstOrDefault(candidate => candidate.Id == job.Id);
            if (existing is not null)
            {
                return Task.FromResult((existing, false));
            }

            _jobs.Add(job);
            return Task.FromResult((job, true));
        }

        public Task<RobotJob?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(_jobs.FirstOrDefault(job => job.Id == id));

        public Task<RobotJob?> NextQueuedAsync(CancellationToken cancellationToken) => Task.FromResult(_jobs.FirstOrDefault(job => job.State == RobotJobState.Queued));

        public Task UpdateAsync(RobotJob job, CancellationToken cancellationToken)
        {
            _jobs[_jobs.FindIndex(candidate => candidate.Id == job.Id)] = job;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RobotJob>> RecentAsync(int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RobotJob>>(_jobs.TakeLast(count).Reverse().ToArray());

        public Task<IReadOnlyList<RobotJob>> UnreportedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RobotJob>>(_jobs.Where(job => job.IsFinished && !job.Reported).ToArray());
    }
}
