namespace Prescriva.Agent.Application.Jobs;

public enum RobotJobKind
{
    LaunchQuote,
}

public enum RobotJobState
{
    Queued,
    Running,
    Completed,
    Stopped,
    Cancelled,
}

/// <summary>
/// A job for the robot (plan 22). <see cref="Request"/> and <see cref="Result"/> are the contract
/// JSON and hold patient and prescription data: stored encrypted, never logged.
/// <see cref="Summary"/> has no values ("2 fórmula(s)"). <see cref="Reported"/> is set once the
/// result reached the CRM.
/// </summary>
public sealed record RobotJob(
    string Id,
    RobotJobKind Kind,
    RobotJobState State,
    string Request,
    string? Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Summary,
    bool Reported = false)
{
    public bool IsFinished => State is RobotJobState.Completed or RobotJobState.Stopped or RobotJobState.Cancelled;

    public static RobotJob Queued(string id, RobotJobKind kind, string request, string summary)
    {
        var now = DateTimeOffset.Now;
        return new RobotJob(id, kind, RobotJobState.Queued, request, null, now, now, summary);
    }
}

/// <summary>The robot's queue of jobs. Adding a job whose ID is already there returns the one there (the same job sent twice runs once).</summary>
public interface IJobStore
{
    Task<(RobotJob Job, bool Added)> AddAsync(RobotJob job, CancellationToken cancellationToken);

    Task<RobotJob?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>The oldest queued job.</summary>
    Task<RobotJob?> NextQueuedAsync(CancellationToken cancellationToken);

    Task UpdateAsync(RobotJob job, CancellationToken cancellationToken);

    /// <summary>The latest jobs, newest first.</summary>
    Task<IReadOnlyList<RobotJob>> RecentAsync(int count, CancellationToken cancellationToken);

    /// <summary>Finished jobs whose result has not reached the CRM yet.</summary>
    Task<IReadOnlyList<RobotJob>> UnreportedAsync(CancellationToken cancellationToken);
}

public enum RobotLevel
{
    /// <summary>Fills the first formula and stops before OK: a person checks and saves.</summary>
    Assisted,

    /// <summary>Saves every formula; the answer to the customer is still approved by a person.</summary>
    Saves,
}

/// <summary>How the robot works at this pharmacy.</summary>
public sealed record RobotSettings(string EmployeeCode, RobotLevel Level, string QuotePlaybookId = "formulacerta_lancar_formula");
