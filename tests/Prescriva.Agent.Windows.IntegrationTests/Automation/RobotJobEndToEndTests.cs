using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Prescriva.Agent.Application.Jobs;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Infrastructure.Jobs;
using Prescriva.Agent.Infrastructure.Playbooks;
using Prescriva.Agent.Infrastructure.Security;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// The whole robot path on the TestTarget quote screen (plan 22): a job posted to the local API
/// is queued encrypted, run by the robot through the real Win32 driver, and its result read back
/// through the API - the way the CRM uses the Agent.
/// </summary>
public sealed class RobotJobEndToEndTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "prescriva-robot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_job_posted_by_the_CRM_is_typed_into_the_quote_screen_and_its_state_comes_back()
    {
        using var target = TestTargetLauncher.Launch("win32-quote");
        var store = new SqliteJobStore(Path.Combine(_directory, "robot.db"), new DpapiPayloadProtector());
        var policies = new JsonDialogPolicyStore(Path.Combine(_directory, "playbooks"));
        var port = FreePort();
        using var api = new LocalJobApi(port, "segredo", store, () => { });
        api.Start();
        var processor = new JobProcessor(store, _ => Task.FromResult(QuoteScreen()), policies,
            () => new RobotSettings("19", RobotLevel.Assisted, "orcamento_teste"),
            new PlaybookRunner(new Win32PlaybookDriver(), PlaybookRunnerOptions.Default).RunAsync);
        using var http = new HttpClient();

        var posted = await Send(http, HttpMethod.Post, $"http://127.0.0.1:{port}/v1/jobs/launch-quote", """
            { "jobId": "crm-1", "formulas": [ { "volume": "60", "volumeUnit": "CAP",
              "components": [ { "code": "25531", "quantity": "75", "unit": "MG" }, { "code": "999", "quantity": "5", "unit": "MG" } ] } ] }
            """);
        var job = await processor.ProcessNextAsync(CancellationToken.None);
        var state = JsonDocument.Parse(await (await Send(http, HttpMethod.Get, $"http://127.0.0.1:{port}/v1/jobs/crm-1", null)).Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        Assert.NotNull(job);
        if (OcrFieldCapture.AvailableLanguages().Count == 0)
        {
            Assert.Equal("stopped", state.GetProperty("state").GetString()); // the stock notice cannot be read: stops safely
            return;
        }

        Assert.True(state.GetProperty("state").GetString() == "completed", state.ToString());
        var screen = new Win32PlaybookDriver();
        Assert.True(await screen.AttachAsync("Prescriva.Agent.TestTarget.exe", "Prescriva Orçamento", CancellationToken.None));
        Assert.Equal("emp=19;rows=25531|75|MG;999|5|MG", await screen.ReadTextAsync("TPrescrivaPanel[1]/TPrescrivaGrid[0]", CancellationToken.None));
    }

    [Fact]
    public async Task A_notice_the_robot_does_not_know_stops_the_job_and_is_offered_to_the_pharmacy()
    {
        using var target = TestTargetLauncher.Launch("win32-quote");
        var store = new SqliteJobStore(Path.Combine(_directory, "robot.db"), new DpapiPayloadProtector());
        var policies = new JsonDialogPolicyStore(Path.Combine(_directory, "playbooks"));
        var processor = new JobProcessor(store, _ => Task.FromResult(QuoteScreen()), policies,
            () => new RobotSettings("19", RobotLevel.Assisted, "orcamento_teste"),
            new PlaybookRunner(new Win32PlaybookDriver(), PlaybookRunnerOptions.Default).RunAsync);
        var request = new LaunchQuoteRequest("crm-2", null, [new QuoteFormula("60", "CAP", [new QuoteComponent("666", "1", "MG")])]);
        await store.AddAsync(RobotJob.Queued("crm-2", RobotJobKind.LaunchQuote, JobJson.Write(request), "1 fórmula(s)"), CancellationToken.None);

        var job = await processor.ProcessNextAsync(CancellationToken.None);

        Assert.Equal(RobotJobState.Stopped, job!.State);
        Assert.Contains("UnknownDialog", job.Result, StringComparison.Ordinal);
        var seen = Assert.Single(await policies.SeenAsync("orcamento_teste", CancellationToken.None));
        Assert.Equal("Atenção!", seen.TitleStartsWith);
        Assert.Contains("OK", seen.Buttons);
    }

    private static Playbook QuoteScreen() => new(
        "orcamento_teste",
        "Orçamento (TestTarget)",
        "Prescriva.Agent.TestTarget.exe",
        "Prescriva Orçamento",
        [
            new PlaybookStep("volume", PlaybookStepKind.SetField, "TPrescrivaPanel[0]/Edit[0]", "{volume}"),
            new PlaybookStep("volume_unidade", PlaybookStepKind.SetField, "TPrescrivaPanel[0]/Edit[1]", "{volume_unidade}"),
            new PlaybookStep("componentes", PlaybookStepKind.GridRows, "TPrescrivaPanel[1]/TPrescrivaGrid[0]", Rows: "componentes", Cells: ["{codigo}", "{quantidade}", "{unidade}"]),
        ],
        [
            new DialogRule("funcionario", "TPrescrivaPedeFuncionario", "Funcionário Recepção", Response: DialogResponse.FillAndPress, Button: "OK", Field: "Edit[0]", Value: "{funcionario}"),
            new DialogRule("estoque_minimo", "TPrescrivaMessage", "Atenção", "Estoque Mínimo", DialogResponse.Press, "OK"),
        ]);

    private static async Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string url, string? json)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "segredo");
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await http.SendAsync(request);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
