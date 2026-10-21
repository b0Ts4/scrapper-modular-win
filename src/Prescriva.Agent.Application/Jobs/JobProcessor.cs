using System.Collections.Immutable;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Application.Jobs;

/// <summary>
/// Runs the robot's next queued job (plan 22): one playbook run per formula, with the pharmacy's
/// notice choices merged in. The first formula that stops stops the job. Its notice, when unknown,
/// is remembered for the pharmacy to decide on, and the screen is left as it is. The result (values
/// read back, or where and why it stopped) is stored with the job for the CRM. Cancelling (the
/// robot's Parar) ends the job as cancelled.
/// </summary>
public sealed class JobProcessor(
    IJobStore store,
    Func<string, Task<Playbook>> playbookFor,
    IDialogPolicyStore policies,
    Func<RobotSettings> settings,
    Func<Playbook, PlaybookData, CancellationToken, Task<PlaybookRunResult>> run)
{
    /// <summary>A job started running (for the banner shown while the robot works).</summary>
    public event Action<RobotJob>? Started;

    public async Task<RobotJob?> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var job = await store.NextQueuedAsync(CancellationToken.None).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        job = job with { State = RobotJobState.Running, UpdatedAt = DateTimeOffset.Now };
        await store.UpdateAsync(job, CancellationToken.None).ConfigureAwait(false);
        Started?.Invoke(job);

        var outcome = await RunAsync(job, cancellationToken).ConfigureAwait(false);
        job = job with { State = outcome.State, Result = JobJson.Write(outcome.Document), UpdatedAt = DateTimeOffset.Now };
        await store.UpdateAsync(job, CancellationToken.None).ConfigureAwait(false);
        return job;
    }

    private async Task<(RobotJobState State, JobResultDocument Document)> RunAsync(RobotJob job, CancellationToken cancellationToken)
    {
        var current = settings();
        LaunchQuoteRequest request;
        IReadOnlyList<PlaybookData> runs;
        try
        {
            request = JobJson.ReadLaunchQuote(job.Request);
            runs = QuoteRuns.Build(request, current);
        }
        catch (JobRequestException invalid)
        {
            return Stopped(job.Id, [], new JobStop(0, null, "InvalidRequest", invalid.Code), null);
        }

        var shipped = await playbookFor(current.QuotePlaybookId).ConfigureAwait(false);
        var playbook = shipped.WithPolicy(await policies.LoadAsync(shipped.Id, CancellationToken.None).ConfigureAwait(false));
        var formulas = ImmutableArray.CreateBuilder<ImmutableDictionary<string, string>>();
        for (var index = 0; index < runs.Count; index++)
        {
            PlaybookRunResult result;
            try
            {
                result = await run(playbook, runs[index], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (RobotJobState.Cancelled, new JobResultDocument(job.Id, "cancelled", formulas.ToImmutable(), new JobStop(index, null, "Cancelled", null)));
            }

            if (result.Status == PlaybookRunStatus.Completed)
            {
                formulas.Add(result.Captured);
                continue;
            }

            if (result.Unknown is { } unknown)
            {
                await policies.RecordSeenAsync(shipped.Id, unknown, CancellationToken.None).ConfigureAwait(false);
            }

            var stop = new JobStop(index, result.StepId, result.Reason?.ToString() ?? "Stopped", result.Detail);
            var notice = result.Unknown is null ? null : JobNotice.From(result.Unknown);
            return result.Reason == PlaybookStopReason.Cancelled || cancellationToken.IsCancellationRequested
                ? (RobotJobState.Cancelled, new JobResultDocument(job.Id, "cancelled", formulas.ToImmutable(), stop))
                : Stopped(job.Id, formulas.ToImmutable(), stop, notice);
        }

        return (RobotJobState.Completed, new JobResultDocument(job.Id, "completed", formulas.ToImmutable()));
    }

    private static (RobotJobState, JobResultDocument) Stopped(string jobId, ImmutableArray<ImmutableDictionary<string, string>> formulas, JobStop stop, JobNotice? notice) =>
        (RobotJobState.Stopped, new JobResultDocument(jobId, "stopped", formulas, stop, notice));
}
