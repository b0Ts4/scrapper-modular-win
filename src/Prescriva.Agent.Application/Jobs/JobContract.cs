using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Prescriva.Agent.Application.Playbooks;

namespace Prescriva.Agent.Application.Jobs;

/// <summary>
/// Contract v0 "launch_quote" (design §5): the formulas a person confirmed in the CRM, with
/// FórmulaCerta product codes. Numbers are written the Brazilian way ("0,5").
/// </summary>
public sealed record LaunchQuoteRequest(string JobId, string? OrderId, ImmutableArray<QuoteFormula> Formulas);

public sealed record QuoteFormula(
    string Volume,
    string VolumeUnit,
    ImmutableArray<QuoteComponent> Components,
    bool? Qsp = null,
    string? Dosage = null);

public sealed record QuoteComponent(string Code, string Quantity, string Unit, string? ExpectedDescription = null);

/// <summary>What the CRM gets back for a job.</summary>
public sealed record JobResultDocument(
    string JobId,
    string State,
    ImmutableArray<ImmutableDictionary<string, string>> Formulas,
    JobStop? Stop = null,
    JobNotice? UnknownNotice = null);

/// <summary>Where the robot stopped: the formula (0-based), the step, a reason code and a detail without values.</summary>
public sealed record JobStop(int Formula, string? Step, string Reason, string? Detail);

/// <summary>A notice the robot did not know, as it read it from the screen.</summary>
public sealed record JobNotice(string ClassName, string Title, ImmutableArray<string> Buttons, string? Text)
{
    public static JobNotice From(UnknownDialog dialog) => new(dialog.ClassName, dialog.Title, dialog.Buttons, dialog.Text);
}

/// <summary>Reads and writes the contract JSON: camelCase, unknown or repeated properties refused.</summary>
public static class JobJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static LaunchQuoteRequest ReadLaunchQuote(string json)
    {
        LaunchQuoteRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<LaunchQuoteRequest>(json, Options);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new JobRequestException("MALFORMED_JSON");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.JobId) || request.JobId.Length > 128)
        {
            throw new JobRequestException("MISSING_JOB_ID");
        }

        return request with { Formulas = request.Formulas.IsDefault ? [] : request.Formulas };
    }

    public static string Write(LaunchQuoteRequest request) => JsonSerializer.Serialize(request, Options);

    public static string Write(JobResultDocument result) => JsonSerializer.Serialize(result, Options);
}

/// <summary>A job request that cannot be used; <see cref="Code"/> says why (never values).</summary>
public sealed class JobRequestException(string code) : Exception("Invalid job request: " + code)
{
    public string Code { get; } = code;
}
