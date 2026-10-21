using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Prescriva.Agent.Application.Jobs;
using Prescriva.Agent.Infrastructure.Jobs;

namespace Prescriva.Agent.Infrastructure.Tests.Jobs;

/// <summary>Jobs reach the robot through its local API or from the CRM, and results go back (plan 22).</summary>
public sealed class JobConnectionTests : IDisposable
{
    private const string Token = "token-de-teste";
    private const string Request = """
        { "jobId": "crm-42", "orderId": "360281",
          "formulas": [ { "volume": "60", "volumeUnit": "CAP", "components": [ { "code": "25531", "quantity": "75", "unit": "MG" } ] } ] }
        """;

    private readonly MemoryStore _store = new();
    private readonly HttpClient _http = new();

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task A_job_posted_to_the_local_API_is_queued_once_and_its_state_can_be_read()
    {
        var port = FreePort();
        var added = 0;
        using var api = new LocalJobApi(port, Token, _store, () => added++);
        api.Start();

        var first = await Post($"http://127.0.0.1:{port}/v1/jobs/launch-quote", Request, Token);
        var again = await Post($"http://127.0.0.1:{port}/v1/jobs/launch-quote", Request, Token);
        var state = await Get($"http://127.0.0.1:{port}/v1/jobs/crm-42", Token);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode); // already there: not queued twice
        Assert.Equal(1, added);
        Assert.Single(_store.Jobs);
        var body = JsonDocument.Parse(await state.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("queued", body.GetProperty("state").GetString());
    }

    [Fact]
    public async Task The_local_API_needs_its_token()
    {
        var port = FreePort();
        using var api = new LocalJobApi(port, Token, _store, () => { });
        api.Start();

        var response = await Post($"http://127.0.0.1:{port}/v1/jobs/launch-quote", Request, "outro");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_store.Jobs);
    }

    [Fact]
    public async Task A_malformed_job_is_refused_with_the_reason()
    {
        var port = FreePort();
        using var api = new LocalJobApi(port, Token, _store, () => { });
        api.Start();

        var response = await Post($"http://127.0.0.1:{port}/v1/jobs/launch-quote", """{ "formulas": [] }""", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("MISSING_JOB_ID", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_job_shows_its_result_through_the_local_API()
    {
        var port = FreePort();
        using var api = new LocalJobApi(port, Token, _store, () => { });
        api.Start();
        await Post($"http://127.0.0.1:{port}/v1/jobs/launch-quote", Request, Token);
        var job = _store.Jobs[0];
        await _store.UpdateAsync(job with { State = RobotJobState.Completed, Result = """{"jobId":"crm-42","state":"completed","formulas":[{"numero_orcamento":"899200"}]}""" }, CancellationToken.None);

        var body = JsonDocument.Parse(await (await Get($"http://127.0.0.1:{port}/v1/jobs/crm-42", Token)).Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("completed", body.GetProperty("state").GetString());
        Assert.Equal("899200", body.GetProperty("result").GetProperty("formulas")[0].GetProperty("numero_orcamento").GetString());
    }

    [Fact]
    public async Task Jobs_are_pulled_from_the_CRM_and_results_are_posted_back_once()
    {
        using var crm = new FakeCrm(Request);
        var connector = new CrmJobConnector(_http, crm.Address, Token, _store);

        var pulled = await connector.PullAsync(CancellationToken.None);
        await _store.UpdateAsync(_store.Jobs[0] with { State = RobotJobState.Completed, Result = """{"jobId":"crm-42","state":"completed"}""" }, CancellationToken.None);
        var pushed = await connector.PushResultsAsync(CancellationToken.None);
        var pushedAgain = await connector.PushResultsAsync(CancellationToken.None);

        Assert.Equal(1, pulled);
        Assert.Equal("crm-42", _store.Jobs[0].Id);
        Assert.Equal(1, pushed);
        Assert.Equal(0, pushedAgain);
        Assert.Equal(["Bearer " + Token], crm.Authorizations.Distinct());
        Assert.Equal("""{"jobId":"crm-42","state":"completed"}""", Assert.Single(crm.Results));
    }

    [Fact]
    public async Task A_result_the_CRM_did_not_take_is_sent_again_later()
    {
        using var crm = new FakeCrm(Request) { RefuseResults = true };
        var connector = new CrmJobConnector(_http, crm.Address, Token, _store);
        await connector.PullAsync(CancellationToken.None);
        await _store.UpdateAsync(_store.Jobs[0] with { State = RobotJobState.Stopped, Result = "{}" }, CancellationToken.None);

        Assert.Equal(0, await connector.PushResultsAsync(CancellationToken.None));
        Assert.False(_store.Jobs[0].Reported);
    }

    private async Task<HttpResponseMessage> Post(string url, string json, string token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(message);
    }

    private async Task<HttpResponseMessage> Get(string url, string token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(message);
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The CRM side of contract v0: one job to hand out, then none; results collected.</summary>
    private sealed class FakeCrm : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _job;
        private bool _handedOut;

        public FakeCrm(string job)
        {
            _job = job;
            Address = new Uri($"http://127.0.0.1:{FreePort()}/");
            _listener.Prefixes.Add(Address.ToString());
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public Uri Address { get; }

        public bool RefuseResults { get; init; }

        public List<string> Authorizations { get; } = [];

        public List<string> Results { get; } = [];

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                lock (Authorizations)
                {
                    Authorizations.Add(context.Request.Headers["Authorization"] ?? string.Empty);
                }

                var path = context.Request.Url!.AbsolutePath;
                if (path == "/v1/agent/jobs/next" && !_handedOut)
                {
                    _handedOut = true;
                    await Reply(context, 200, $$"""{ "kind": "launch_quote", "job": {{_job}} }""");
                }
                else if (path == "/v1/agent/jobs/next")
                {
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                }
                else if (path.StartsWith("/v1/agent/jobs/", StringComparison.Ordinal) && path.EndsWith("/result", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    if (RefuseResults)
                    {
                        await Reply(context, 503, "{}");
                    }
                    else
                    {
                        lock (Results)
                        {
                            Results.Add(body);
                        }

                        await Reply(context, 200, "{}");
                    }
                }
                else
                {
                    await Reply(context, 404, "{}");
                }
            }
        }

        private static async Task Reply(HttpListenerContext context, int status, string json)
        {
            context.Response.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private sealed class MemoryStore : IJobStore
    {
        public List<RobotJob> Jobs { get; } = [];

        public Task<(RobotJob Job, bool Added)> AddAsync(RobotJob job, CancellationToken cancellationToken)
        {
            lock (Jobs)
            {
                var existing = Jobs.FirstOrDefault(candidate => candidate.Id == job.Id);
                if (existing is not null)
                {
                    return Task.FromResult((existing, false));
                }

                Jobs.Add(job);
                return Task.FromResult((job, true));
            }
        }

        public Task<RobotJob?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(Jobs.FirstOrDefault(job => job.Id == id));

        public Task<RobotJob?> NextQueuedAsync(CancellationToken cancellationToken) => Task.FromResult(Jobs.FirstOrDefault(job => job.State == RobotJobState.Queued));

        public Task UpdateAsync(RobotJob job, CancellationToken cancellationToken)
        {
            Jobs[Jobs.FindIndex(candidate => candidate.Id == job.Id)] = job;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RobotJob>> RecentAsync(int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RobotJob>>(Jobs.ToArray());

        public Task<IReadOnlyList<RobotJob>> UnreportedAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RobotJob>>(Jobs.Where(job => job.IsFinished && !job.Reported).ToArray());
    }
}
