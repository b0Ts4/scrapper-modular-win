using System.Collections.Immutable;
using Prescriva.Agent.Application.Jobs;

namespace Prescriva.Agent.Application.Tests.Jobs;

/// <summary>A "launch this quote" request becomes one playbook run per formula (plan 22).</summary>
public sealed class QuoteRunsTests
{
    [Fact]
    public void Each_formula_becomes_a_run_and_only_the_last_says_no_more_formulas()
    {
        var runs = QuoteRuns.Build(Request(2), new RobotSettings("19", RobotLevel.Saves));

        Assert.Equal(2, runs.Count);
        Assert.Equal("Sim", runs[0].Values["mais_formulas"]);
        Assert.Equal("Não", runs[1].Values["mais_formulas"]);
        Assert.All(runs, run => Assert.Equal("sim", run.Values["salvar"]));
        Assert.Equal("19", runs[0].Values["funcionario"]);
        Assert.Equal("60", runs[0].Values["volume"]);
        Assert.Equal("CAP", runs[0].Values["volume_unidade"]);
        Assert.Equal("Não", runs[0].Values["qsp"]);
        Assert.Equal(["25531", "7995"], runs[0].Lists["componentes"].Select(row => row["codigo"]));
        Assert.Equal("5", runs[0].Lists["componentes"][1]["quantidade"]);
    }

    [Fact]
    public void Assisted_fills_only_the_first_formula_and_does_not_save()
    {
        var runs = QuoteRuns.Build(Request(2), new RobotSettings("19", RobotLevel.Assisted));

        var run = Assert.Single(runs);
        Assert.Equal("não", run.Values["salvar"]);
    }

    [Fact]
    public void Q_S_P_and_dosage_come_from_the_formula_when_given()
    {
        var request = Request(1);
        request = request with { Formulas = [request.Formulas[0] with { Qsp = true, Dosage = "1 CAPSULA AO DIA" }] };

        var run = QuoteRuns.Build(request, new RobotSettings("19", RobotLevel.Saves)).Single();

        Assert.Equal("Sim", run.Values["qsp"]);
        Assert.Equal("1 CAPSULA AO DIA", run.Values["posologia"]);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void A_request_without_formulas_or_components_is_refused(int formulas, int components)
    {
        var request = new LaunchQuoteRequest("job-1", null,
            Enumerable.Range(0, formulas).Select(_ => new QuoteFormula("60", "CAP", Components(components))).ToImmutableArray());

        Assert.Throws<JobRequestException>(() => QuoteRuns.Build(request, new RobotSettings("19", RobotLevel.Saves)));
    }

    [Fact]
    public void Requests_are_read_from_the_contract_json()
    {
        var request = JobJson.ReadLaunchQuote("""
            { "jobId": "crm-42", "orderId": "360281",
              "formulas": [ { "volume": "60", "volumeUnit": "CAP",
                              "components": [ { "code": "25531", "quantity": "75", "unit": "MG", "expectedDescription": "PREGABALINA" } ] } ] }
            """);

        Assert.Equal("crm-42", request.JobId);
        Assert.Equal("PREGABALINA", request.Formulas[0].Components[0].ExpectedDescription);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "orderId": "1", "formulas": [] }""")]
    [InlineData("""{ "jobId": "x", "formulas": [], "unknown": 1 }""")]
    public void A_malformed_request_is_refused(string json)
    {
        Assert.Throws<JobRequestException>(() => JobJson.ReadLaunchQuote(json));
    }

    internal static LaunchQuoteRequest Request(int formulas) => new(
        "job-1",
        "360281",
        Enumerable.Range(0, formulas).Select(_ => new QuoteFormula("60", "CAP", Components(2))).ToImmutableArray());

    private static ImmutableArray<QuoteComponent> Components(int count) =>
        new[] { new QuoteComponent("25531", "75", "MG"), new QuoteComponent("7995", "5", "MG") }.Take(count).ToImmutableArray();
}
