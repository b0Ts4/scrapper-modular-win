using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Prescriva.Agent.Application.Jobs;

namespace Prescriva.Agent.Infrastructure.Jobs;

/// <summary>
/// The Agent's side of the CRM link (plan 22): the Agent connects out (no port opened at the
/// pharmacy), pulls jobs with <c>GET {crm}/v1/agent/jobs/next</c> (204 = none) and posts each
/// finished job's result to <c>POST {crm}/v1/agent/jobs/{id}/result</c>; a result the CRM did not
/// take is sent again on the next round. Same job twice is queued once.
/// </summary>
public sealed class CrmJobConnector(HttpClient http, Uri crm, string token, IJobStore store) : IJobConnection
{
    private const int MaxJobsPerRound = 10;

    /// <summary>Queues the CRM's waiting jobs; returns how many were new.</summary>
    public async Task<int> PullAsync(CancellationToken cancellationToken)
    {
        var added = 0;
        for (var round = 0; round < MaxJobsPerRound; round++)
        {
            using var response = await http.SendAsync(Request(HttpMethod.Get, "v1/agent/jobs/next"), cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
            {
                break;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != "launch_quote" ||
                !document.RootElement.TryGetProperty("job", out var job))
            {
                break;
            }

            var request = JobJson.ReadLaunchQuote(job.GetRawText());
            var (_, isNew) = await store.AddAsync(
                RobotJob.Queued(request.JobId, RobotJobKind.LaunchQuote, JobJson.Write(request), $"{request.Formulas.Length} fórmula(s)"),
                cancellationToken).ConfigureAwait(false);
            if (!isNew)
            {
                break; // the CRM is handing out a job already here: nothing new behind it this round
            }

            added++;
        }

        return added;
    }

    /// <summary>Posts the results of finished jobs not yet reported; returns how many the CRM took.</summary>
    public async Task<int> PushResultsAsync(CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var job in await store.UnreportedAsync(cancellationToken).ConfigureAwait(false))
        {
            using var request = Request(HttpMethod.Post, $"v1/agent/jobs/{Uri.EscapeDataString(job.Id)}/result");
            request.Content = new StringContent(job.Result ?? "{}", Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await store.UpdateAsync(job with { Reported = true }, cancellationToken).ConfigureAwait(false);
                sent++;
            }
        }

        return sent;
    }

    private HttpRequestMessage Request(HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method, new Uri(crm, relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
