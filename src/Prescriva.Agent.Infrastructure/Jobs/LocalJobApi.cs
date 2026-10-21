using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prescriva.Agent.Application.Jobs;

namespace Prescriva.Agent.Infrastructure.Jobs;

/// <summary>
/// The robot's local API (plan 22), on 127.0.0.1 only, with a bearer token:
/// <c>POST /v1/jobs/launch-quote</c> queues a job (contract v0; the same job ID twice is queued
/// once), <c>GET /v1/jobs/{id}</c> returns its state and result, <c>GET /v1/jobs</c> the latest
/// jobs without values. Request bodies are capped; nothing received is logged.
/// </summary>
public sealed class LocalJobApi : IDisposable
{
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly HttpListener _listener = new();
    private readonly byte[] _token;
    private readonly IJobStore _store;
    private readonly Action _jobAdded;
    private readonly CancellationTokenSource _stopping = new();

    public LocalJobApi(int port, string token, IJobStore store, Action jobAdded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _token = Encoding.UTF8.GetBytes("Bearer " + token);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _jobAdded = jobAdded ?? throw new ArgumentNullException(nameof(jobAdded));
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        _stopping.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (!Authorized(context.Request))
            {
                await ReplyAsync(context, 401, new { error = "UNAUTHORIZED" }).ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url!.AbsolutePath.TrimEnd('/');
            var method = context.Request.HttpMethod;
            if (method == "POST" && path == "/v1/jobs/launch-quote")
            {
                await LaunchQuoteAsync(context).ConfigureAwait(false);
            }
            else if (method == "GET" && path == "/v1/jobs")
            {
                var jobs = await _store.RecentAsync(50, _stopping.Token).ConfigureAwait(false);
                await ReplyAsync(context, 200, jobs.Select(job => new { jobId = job.Id, state = State(job), summary = job.Summary, updatedAt = job.UpdatedAt })).ConfigureAwait(false);
            }
            else if (method == "GET" && path.StartsWith("/v1/jobs/", StringComparison.Ordinal))
            {
                var job = await _store.GetAsync(Uri.UnescapeDataString(path["/v1/jobs/".Length..]), _stopping.Token).ConfigureAwait(false);
                if (job is null)
                {
                    await ReplyAsync(context, 404, new { error = "JOB_NOT_FOUND" }).ConfigureAwait(false);
                    return;
                }

                await ReplyAsync(context, 200, new
                {
                    jobId = job.Id,
                    state = State(job),
                    summary = job.Summary,
                    updatedAt = job.UpdatedAt,
                    result = job.Result is null ? (JsonElement?)null : JsonDocument.Parse(job.Result).RootElement,
                }).ConfigureAwait(false);
            }
            else
            {
                await ReplyAsync(context, 404, new { error = "NOT_FOUND" }).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The caller went away.
        }
    }

    private async Task LaunchQuoteAsync(HttpListenerContext context)
    {
        if (context.Request.ContentLength64 > MaxBodyBytes)
        {
            await ReplyAsync(context, 413, new { error = "TOO_LARGE" }).ConfigureAwait(false);
            return;
        }

        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            var buffer = new char[MaxBodyBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read > MaxBodyBytes)
            {
                await ReplyAsync(context, 413, new { error = "TOO_LARGE" }).ConfigureAwait(false);
                return;
            }

            body = new string(buffer, 0, read);
        }

        LaunchQuoteRequest request;
        try
        {
            request = JobJson.ReadLaunchQuote(body);
        }
        catch (JobRequestException invalid)
        {
            await ReplyAsync(context, 400, new { error = invalid.Code }).ConfigureAwait(false);
            return;
        }

        var (job, added) = await _store.AddAsync(
            RobotJob.Queued(request.JobId, RobotJobKind.LaunchQuote, JobJson.Write(request), $"{request.Formulas.Length} fórmula(s)"),
            _stopping.Token).ConfigureAwait(false);
        if (added)
        {
            _jobAdded();
        }

        await ReplyAsync(context, added ? 202 : 200, new { jobId = job.Id, state = State(job), added }).ConfigureAwait(false);
    }

    private bool Authorized(HttpListenerRequest request)
    {
        var header = Encoding.UTF8.GetBytes(request.Headers["Authorization"] ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(header, _token);
    }

    private static string State(RobotJob job) => job.State.ToString().ToLowerInvariant();

    private static async Task ReplyAsync(HttpListenerContext context, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JobJson.Options);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }
}
