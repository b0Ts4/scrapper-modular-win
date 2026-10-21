using System.Collections.Immutable;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Windows.Automation;

namespace Prescriva.Agent.Windows.IntegrationTests.Automation;

/// <summary>
/// A playbook typed into a live Delphi-like quote screen (TestTarget <c>win32-quote</c>, plan 19)
/// by <see cref="Win32PlaybookDriver"/> - the way the Agent fills FórmulaCerta's Orçamentos:
/// "Funcionário Recepção" asked from inside the focus change is answered, the stock notice is
/// recognized by its text and answered, an unknown notice stops the run, and the save that Enter
/// on the empty row would start never happens.
/// </summary>
public sealed class PlaybookRunTests
{
    private const string Grid = "TPrescrivaPanel[1]/TPrescrivaGrid[0]";

    [Fact]
    public async Task A_formula_is_filled_through_the_employee_and_stock_dialogs_without_saving()
    {
        using var target = TestTargetLauncher.Launch("win32-quote");
        var driver = await QuoteScreenAsync();

        var result = await new PlaybookRunner(driver).RunAsync(Quote(), Data(("25531", "75", "MG"), ("999", "5", "MG")), CancellationToken.None);

        if (OcrFieldCapture.AvailableLanguages().Count == 0)
        {
            // Without an OCR language the stock notice cannot be recognized: the run stops there, safely.
            Assert.Equal(PlaybookStopReason.UnknownDialog, result.Reason);
            return;
        }

        Assert.True(result.Status == PlaybookRunStatus.Completed, Describe(result));
        Assert.Equal(["funcionario", "estoque_minimo"], result.AnsweredDialogs.ToArray());
        Assert.Equal("emp=19;rows=25531|75|MG;999|5|MG", await driver.ReadTextAsync(Grid, CancellationToken.None));
        Assert.Equal("60", await driver.ReadTextAsync("TPrescrivaPanel[0]/Edit[0]", CancellationToken.None));
    }

    [Fact]
    public async Task A_notice_no_rule_knows_stops_the_run_and_stays_open_for_a_person()
    {
        using var target = TestTargetLauncher.Launch("win32-quote");
        var driver = await QuoteScreenAsync();

        var result = await new PlaybookRunner(driver).RunAsync(Quote(), Data(("666", "1", "MG"), ("25531", "75", "MG")), CancellationToken.None);

        Assert.True(result.Reason == PlaybookStopReason.UnknownDialog, Describe(result));
        Assert.Equal("componentes[0]", result.StepId);
        var open = await driver.DialogsAsync(Playbook.DefaultIgnoredWindowClasses, CancellationToken.None);
        Assert.Contains(open, dialog => dialog.Title == "Atenção!");
        Assert.DoesNotContain("SAVE", await driver.ReadTextAsync(Grid, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_notice_already_open_when_the_robot_starts_is_answered_and_the_other_screens_are_not_notices()
    {
        // A person clicked elsewhere: "Funcionário Recepção" is open and, as in Delphi, every other
        // window of the program (here also the TestTarget's main window) is disabled behind it.
        using var target = TestTargetLauncher.Launch("win32-quote-asking");
        var driver = await QuoteScreenAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((await driver.DialogsAsync(Playbook.DefaultIgnoredWindowClasses, CancellationToken.None)).All(dialog => dialog.Title != "Funcionário Recepção"))
        {
            Assert.True(DateTime.UtcNow < deadline, "The employee dialog did not open.");
            await Task.Delay(200);
            await driver.AttachAsync(Quote().ProcessIdentity, Quote().WindowRule, CancellationToken.None);
        }

        var result = await new PlaybookRunner(new Win32PlaybookDriver()).RunAsync(Quote(), Data(("25531", "75", "MG")), CancellationToken.None);

        Assert.True(result.Status == PlaybookRunStatus.Completed, Describe(result));
        Assert.Equal("funcionario", result.AnsweredDialogs[0]);
    }

    [Fact]
    public async Task A_notice_whose_buttons_are_Delphi_TBitBtn_is_answered()
    {
        // FórmulaCerta's print setup ("Orçamento MODELO3") has TBitBtn buttons, not TButton (2026-10-21).
        using var target = TestTargetLauncher.Launch("win32-quote");
        var driver = await QuoteScreenAsync();
        var playbook = Quote() with { Dialogs = Quote().Dialogs.Add(new DialogRule("impressao", "TPrescrivaImpressao", "Orçamento", Response: DialogResponse.Press, Button: "Cancelar")) };

        var result = await new PlaybookRunner(driver).RunAsync(playbook, Data(("777", "1", "MG")), CancellationToken.None);

        Assert.True(result.Status == PlaybookRunStatus.Completed, Describe(result));
        Assert.Contains("impressao", result.AnsweredDialogs);
    }

    [Fact]
    public async Task The_program_window_not_being_open_stops_at_once()
    {
        var result = await new PlaybookRunner(new Win32PlaybookDriver()).RunAsync(Quote() with { WindowRule = "Janela que não existe" }, Data(("25531", "75", "MG")), CancellationToken.None);

        Assert.Equal(PlaybookStopReason.WindowNotFound, result.Reason);
    }

    /// <summary>The TestTarget shows its quote screen once its main window has loaded: wait for it.</summary>
    private static async Task<Win32PlaybookDriver> QuoteScreenAsync()
    {
        var driver = new Win32PlaybookDriver();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!await driver.AttachAsync(Quote().ProcessIdentity, Quote().WindowRule, CancellationToken.None))
        {
            Assert.True(DateTime.UtcNow < deadline, "The quote screen did not open.");
            await Task.Delay(200);
        }

        await Task.Delay(500);
        return driver;
    }

    private static string Describe(PlaybookRunResult result) =>
        $"{result.Status} at {result.StepId}: {result.Reason} {result.Detail} (answered: {string.Join(", ", result.AnsweredDialogs)})";

    private static Playbook Quote() => new(
        "orcamento_teste",
        "Orçamento (TestTarget)",
        "Prescriva.Agent.TestTarget.exe",
        "Prescriva Orçamento",
        [
            new PlaybookStep("volume", PlaybookStepKind.SetField, "TPrescrivaPanel[0]/Edit[0]", "{volume}"),
            new PlaybookStep("volume_unidade", PlaybookStepKind.SetField, "TPrescrivaPanel[0]/Edit[1]", "{volume_unidade}"),
            new PlaybookStep("componentes", PlaybookStepKind.GridRows, Grid, Rows: "componentes", Cells: ["{codigo}", "{quantidade}", "{unidade}"]),
        ],
        [
            new DialogRule("funcionario", "TPrescrivaPedeFuncionario", "Funcionário Recepção", Response: DialogResponse.FillAndPress, Button: "OK", Field: "Edit[0]", Value: "{funcionario}"),
            new DialogRule("estoque_minimo", "TPrescrivaMessage", "Atenção", "Estoque Mínimo", DialogResponse.Press, "OK"),
        ]);

    private static PlaybookData Data(params (string Code, string Quantity, string Unit)[] rows) => new(
        ImmutableDictionary<string, string>.Empty.Add("volume", "60").Add("volume_unidade", "CAP").Add("funcionario", "19"),
        ImmutableDictionary<string, ImmutableArray<ImmutableDictionary<string, string>>>.Empty.Add(
            "componentes",
            rows.Select(row => ImmutableDictionary<string, string>.Empty.Add("codigo", row.Code).Add("quantidade", row.Quantity).Add("unidade", row.Unit)).ToImmutableArray()));
}
