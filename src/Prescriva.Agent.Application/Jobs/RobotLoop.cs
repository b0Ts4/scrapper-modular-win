namespace Prescriva.Agent.Application.Jobs;

/// <summary>Where jobs come from and results go to (the CRM).</summary>
public interface IJobConnection
{
    Task<int> PullAsync(CancellationToken cancellationToken);

    Task<int> PushResultsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One round of the robot (plan 22): fetch the CRM's jobs, run every queued job one after the
/// other, send the finished jobs' results back. The CRM being unreachable is reported and does not
/// stop the local queue. Stopping (the robot's Parar) cancels the running job and ends the round;
/// the jobs behind it stay queued.
/// </summary>
public sealed class RobotLoop(JobProcessor processor, IJobConnection? connection)
{
    public event Action<RobotJob>? JobStarted;

    public event Action<RobotJob>? JobFinished;

    /// <summary>A message without values ("CRM fora do ar: ...").</summary>
    public event Action<string>? ConnectionProblem;

    public async Task RunRoundAsync(CancellationToken stop)
    {
        await ConnectAsync(connection is null ? null : connection.PullAsync).ConfigureAwait(false);
        processor.Started += OnStarted;
        try
        {
            while (!stop.IsCancellationRequested && await processor.ProcessNextAsync(stop).ConfigureAwait(false) is { } job)
            {
                JobFinished?.Invoke(job);
            }
        }
        finally
        {
            processor.Started -= OnStarted;
        }

        await ConnectAsync(connection is null ? null : connection.PushResultsAsync).ConfigureAwait(false);
    }

    private void OnStarted(RobotJob job) => JobStarted?.Invoke(job);

    private async Task ConnectAsync(Func<CancellationToken, Task<int>>? call)
    {
        if (call is null)
        {
            return;
        }

        try
        {
            await call(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JobRequestException or System.Text.Json.JsonException)
        {
            ConnectionProblem?.Invoke("Não foi possível falar com o CRM: " + exception.GetType().Name);
        }
    }
}
