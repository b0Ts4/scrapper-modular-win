using System.Collections.Immutable;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Application.Tests.Playbooks;

/// <summary>
/// The runner types a playbook into a program through <see cref="IPlaybookDriver"/> and answers
/// the program's dialogs by rule; anything unexpected stops it (plan 19). Behaviours seen on
/// FórmulaCerta on 2026-10-20 are reproduced by the fake program below.
/// </summary>
public sealed class PlaybookRunnerTests
{
    private const string Volume = "TPanel[0]/TEdit[0]";
    private const string Unit = "TPanel[0]/TEdit[1]";
    private const string Grid = "TPanel[1]/TGrid[0]";

    [Fact]
    public async Task Fields_then_grid_rows_are_typed_in_order_and_the_run_completes()
    {
        var program = new FakeProgram();

        var result = await Run(program);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
        Assert.Equal(
            [
                $"set {Volume}=60", $"set {Unit}=CAP",
                $"type {Grid}=25531", $"key {Grid}=Enter", $"type {Grid}=75", $"key {Grid}=Enter", $"type {Grid}=MG", $"key {Grid}=Enter",
                $"type {Grid}=7995", $"key {Grid}=Enter", $"type {Grid}=5", $"key {Grid}=Enter", $"type {Grid}=MG", $"key {Grid}=Enter",
            ],
            program.Actions);
    }

    [Fact]
    public async Task A_number_read_back_in_another_format_is_the_same_value()
    {
        var program = new FakeProgram { ReadBack = { [Volume] = "60,00" } };

        Assert.Equal(PlaybookRunStatus.Completed, (await Run(program)).Status);
    }

    [Fact]
    public async Task A_field_that_reads_back_differently_stops_the_run_at_that_step()
    {
        var program = new FakeProgram { ReadBack = { [Unit] = "ML" } };

        var result = await Run(program);

        Assert.Equal(PlaybookRunStatus.Stopped, result.Status);
        Assert.Equal(PlaybookStopReason.ValueMismatch, result.Reason);
        Assert.Equal("unidade", result.StepId);
        Assert.DoesNotContain(program.Actions, action => action.StartsWith("type", StringComparison.Ordinal));
        Assert.DoesNotContain("ML", result.Detail ?? string.Empty, StringComparison.Ordinal); // never values
    }

    [Fact]
    public async Task A_dialog_no_rule_knows_stops_the_run_and_is_left_open()
    {
        var program = new FakeProgram();
        program.DialogAfter($"key {Grid}=Enter", new OpenDialog(7, "TMessageForm", "Interação", ["OK"]));

        var result = await Run(program);

        Assert.Equal(PlaybookStopReason.UnknownDialog, result.Reason);
        Assert.Equal("componentes[0]", result.StepId);
        Assert.Single(program.Open);
        Assert.DoesNotContain(program.Actions, action => action.StartsWith("press", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dialog_matched_by_title_and_text_is_answered_and_the_run_goes_on()
    {
        var program = new FakeProgram();
        program.DialogAfter($"type {Grid}=MG", new OpenDialog(8, "TMessageForm", "Atenção!", ["OK"]), "Produto abaixo do Estoque Mínimo!");

        var result = await Run(program);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
        Assert.Contains("press 8=OK", program.Actions);
        Assert.Equal(["estoque_minimo"], result.AnsweredDialogs.ToArray());
    }

    [Fact]
    public async Task The_same_title_with_another_text_is_not_that_rule()
    {
        var program = new FakeProgram();
        program.DialogAfter($"type {Grid}=MG", new OpenDialog(8, "TMessageForm", "Atenção!", ["OK"]), "Interação medicamentosa");

        Assert.Equal(PlaybookStopReason.UnknownDialog, (await Run(program)).Reason);
    }

    [Fact]
    public async Task A_dialog_whose_text_cannot_be_read_is_not_matched_by_a_text_rule()
    {
        var program = new FakeProgram();
        program.DialogAfter($"type {Grid}=MG", new OpenDialog(8, "TMessageForm", "Atenção!", ["OK"]), text: null);

        Assert.Equal(PlaybookStopReason.UnknownDialog, (await Run(program)).Reason);
    }

    [Fact]
    public async Task A_stop_rule_stops_with_the_rule_named()
    {
        var program = new FakeProgram();
        program.DialogAfter($"key {Grid}=Enter", new OpenDialog(9, "TfrAtencao", "Atenção!", ["OK", "Cancelar"]), "Quantidade zerada na ficha de pesagem!");

        var result = await Run(program);

        Assert.Equal(PlaybookStopReason.DialogRuleStop, result.Reason);
        Assert.Equal("quantidade_zerada", result.Detail);
    }

    [Fact]
    public async Task Focus_held_by_a_dialog_is_answered_then_the_typing_is_retried()
    {
        // FórmulaCerta asks "Funcionário Recepção" from inside the focus change of the formula.
        var program = new FakeProgram();
        program.BlockOnce($"type {Grid}=25531", new OpenDialog(5, "TfrFuncionario", "Funcionário Recepção", ["OK", "Cancelar"]));

        var result = await Run(program);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
        Assert.Contains("fill 5 TEdit[1]=19", program.Actions);
        Assert.Contains("press 5=OK", program.Actions);
        Assert.Equal(2, program.Actions.Count(action => action == $"type {Grid}=25531"));
    }

    [Fact]
    public async Task The_window_is_waited_for_when_it_opens_a_moment_later()
    {
        // Nova Fórmula opens Orçamentos a moment after the click.
        var program = new FakeProgram { WindowOpensAfterAttempts = 3 };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate with { AttachTimeout = TimeSpan.FromSeconds(5) })
            .RunAsync(Playbook(), Data(), CancellationToken.None);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task An_unknown_dialog_is_reported_with_what_it_shows_so_the_pharmacy_can_decide()
    {
        var program = new FakeProgram();
        program.DialogAfter($"key {Grid}=Enter", new OpenDialog(7, "TMessageForm", "Atenção!", ["Sim", "Não"]), "Interação medicamentosa encontrada");

        var result = await Run(program);

        Assert.NotNull(result.Unknown);
        Assert.Equal("TMessageForm", result.Unknown!.ClassName);
        Assert.Equal("Atenção!", result.Unknown.Title);
        Assert.Equal(["Sim", "Não"], result.Unknown.Buttons.ToArray());
        Assert.Equal("Interação medicamentosa encontrada", result.Unknown.Text);
    }

    [Fact]
    public async Task The_title_of_an_answered_dialog_and_a_dialog_field_are_kept_as_values()
    {
        var program = new FakeProgram { DialogFields = { ["TwwDBEdit[0]"] = "42,00" } };
        program.DialogAfter($"type {Grid}=MG", new OpenDialog(9, "TMessageForm", "Orçamento 899124", ["Sim", "Não"]), "Mais Fórmula(s)?");
        var playbook = Playbook() with
        {
            Dialogs = Playbook().Dialogs.Add(new DialogRule("mais_formulas", "TMessageForm", "Orçamento ", "Mais Fórmula", DialogResponse.Press, "{mais_formulas}",
                CaptureTitleAs: "numero_orcamento", CaptureField: "TwwDBEdit[0]", CaptureAs: "valor")),
        };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(playbook, Data(), CancellationToken.None);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
        Assert.Equal("899124", result.Captured["numero_orcamento"]);
        Assert.Equal("42,00", result.Captured["valor"]);
        Assert.Contains("press 9=Não", program.Actions); // the button came from the data
    }

    [Fact]
    public async Task A_read_step_keeps_the_control_text_as_a_value()
    {
        var program = new FakeProgram { ReadBack = { ["TPanel[0]/TEdit[5]"] = "899124" } };
        var playbook = Playbook() with { Steps = Playbook().Steps.Add(new PlaybookStep("numero", PlaybookStepKind.ReadField, "TPanel[0]/TEdit[5]", CaptureAs: "numero_orcamento")) };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(playbook, Data(), CancellationToken.None);

        Assert.Equal("899124", result.Captured["numero_orcamento"]);
    }

    [Fact]
    public async Task A_button_from_the_data_that_is_forbidden_is_never_pressed()
    {
        var program = new FakeProgram();
        program.DialogAfter($"type {Grid}=MG", new OpenDialog(9, "TMessageForm", "Orçamento 1", ["Imprimir", "Cancelar"]), "Imprimir?");
        var playbook = Playbook() with { Dialogs = Playbook().Dialogs.Add(new DialogRule("imprimir", "TMessageForm", "Orçamento ", null, DialogResponse.Press, "{botao}")) };
        var data = Data() with { Values = Data().Values.Add("botao", "Imprimir") };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(playbook, data, CancellationToken.None);

        Assert.Equal(PlaybookStopReason.ForbiddenButton, result.Reason);
        Assert.DoesNotContain(program.Actions, action => action.StartsWith("press 9", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sim", true)]
    [InlineData("Sim", true)]
    [InlineData("não", false)]
    public async Task A_step_with_a_condition_runs_only_when_the_data_says_yes(string salvar, bool clicked)
    {
        // One playbook for both levels: assisted (stops before OK) and saving (presses OK).
        var program = new FakeProgram { Captions = { ["TPanel[2]/TButton[0]"] = "&OK" } };
        var playbook = Playbook() with { Steps = Playbook().Steps.Add(new PlaybookStep("ok", PlaybookStepKind.Click, "TPanel[2]/TButton[0]", OnlyWhen: "salvar")) };
        var data = Data() with { Values = Data().Values.Add("salvar", salvar) };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(playbook, data, CancellationToken.None);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
        Assert.Equal(clicked, program.Actions.Contains("click TPanel[2]/TButton[0]"));
    }

    [Fact]
    public async Task Typing_interrupted_by_a_dialog_is_answered_but_never_typed_again()
    {
        // Retyping a half-taken grid cell would double the product code ("2553125531").
        var program = new FakeProgram();
        program.InterruptOnce($"type {Grid}=25531", new OpenDialog(8, "TMessageForm", "Atenção!", ["OK"]), "Produto abaixo do Estoque Mínimo!");

        var result = await Run(program);

        Assert.Equal(PlaybookStopReason.InputInterrupted, result.Reason);
        Assert.Equal("componentes[0]", result.StepId);
        Assert.Single(program.Actions, action => action == $"type {Grid}=25531");
        Assert.Contains("press 8=OK", program.Actions);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("1.000")]
    public async Task A_number_written_with_a_decimal_point_stops_before_the_row_is_typed(string quantity)
    {
        // FórmulaCerta reads numbers the Brazilian way: "0.5" would become 5 - a wrong dose.
        var program = new FakeProgram();

        var result = await Run(program, Data(("7995", quantity, "MG")));

        Assert.Equal(PlaybookStopReason.AmbiguousNumber, result.Reason);
        Assert.Equal("componentes[1]", result.StepId);
        Assert.DoesNotContain($"type {Grid}=7995", program.Actions);
    }

    [Fact]
    public async Task A_field_value_written_with_a_decimal_point_stops_before_it_is_typed()
    {
        var program = new FakeProgram();
        var data = Data() with { Values = Data().Values.SetItem("volume", "60.5") };

        var result = await Run(program, data);

        Assert.Equal(PlaybookStopReason.AmbiguousNumber, result.Reason);
        Assert.Equal("volume", result.StepId);
        Assert.Empty(program.Actions);
    }

    [Fact]
    public async Task An_accelerator_step_opens_a_new_quote_and_its_dialog_is_answered()
    {
        // The robot opens "Novo Orç." itself; the "Requisição" dialog is answered, then it fills.
        var program = new FakeProgram();
        program.DialogAfter("accel N", new OpenDialog(3, "TfrPedeOrc", "Requisição", ["OK", "Cancelar"]));
        var playbook = Playbook() with
        {
            Steps = Playbook().Steps.Insert(0, new PlaybookStep("abrir", PlaybookStepKind.Accelerator, string.Empty, "N", OnlyWhen: "abrir")),
            Dialogs = Playbook().Dialogs.Add(new DialogRule("novo_orcamento", "TfrPedeOrc", "Requisição", Response: DialogResponse.Press, Button: "OK")),
        };
        var data = Data() with { Values = Data().Values.Add("abrir", "sim") };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(playbook, data, CancellationToken.None);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
        Assert.Contains("accel N", program.Actions);
        Assert.Contains("novo_orcamento", result.AnsweredDialogs.ToArray());
    }

    [Fact]
    public async Task The_accelerator_step_is_skipped_when_the_data_does_not_ask_to_open()
    {
        var program = new FakeProgram();
        var playbook = Playbook() with { Steps = Playbook().Steps.Insert(0, new PlaybookStep("abrir", PlaybookStepKind.Accelerator, string.Empty, "N", OnlyWhen: "abrir")) };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(playbook, Data(), CancellationToken.None);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
        Assert.DoesNotContain(program.Actions, action => action.StartsWith("accel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_click_point_step_opens_a_new_quote_and_its_dialog_is_answered()
    {
        // "Novo Orç." is a toolbar button with no handle nor accelerator: the robot clicks it by a point.
        var program = new FakeProgram();
        program.DialogAfter("clickpoint TCoolBar[0]/TToolBar[0]@20,23", new OpenDialog(3, "TfrPedeOrc", "Requisição", ["OK", "Cancelar"]));
        var playbook = Playbook() with
        {
            Steps = Playbook().Steps.Insert(0, new PlaybookStep("abrir", PlaybookStepKind.ClickPoint, "TCoolBar[0]/TToolBar[0]", "20,23", OnlyWhen: "abrir")),
            Dialogs = Playbook().Dialogs.Add(new DialogRule("novo_orcamento", "TfrPedeOrc", "Requisição", Response: DialogResponse.Press, Button: "OK")),
        };
        var data = Data() with { Values = Data().Values.Add("abrir", "sim") };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(playbook, data, CancellationToken.None);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
        Assert.Contains("clickpoint TCoolBar[0]/TToolBar[0]@20,23", program.Actions);
        Assert.Contains("novo_orcamento", result.AnsweredDialogs.ToArray());
    }

    [Fact]
    public async Task A_field_held_by_two_notices_in_a_row_is_typed_once_both_are_answered()
    {
        // Nova Fórmula (MobyPharma, 2026-10-21): "novo orçamento" then "Funcionário Recepção" before the first field.
        var program = new FakeProgram();
        program.BlockWith($"set {Volume}=60",
            new OpenDialog(4, "TfrPedeOrc", "Requisição", ["OK", "Cancelar"]),
            new OpenDialog(5, "TfrFuncionario", "Funcionário Recepção", ["OK", "Cancelar"]));
        var playbook = Playbook() with { Dialogs = Playbook().Dialogs.Add(new DialogRule("novo_orcamento", "TfrPedeOrc", "Requisição", Response: DialogResponse.Press, Button: "OK")) };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(playbook, Data(), CancellationToken.None);

        Assert.Equal(PlaybookRunStatus.Completed, result.Status);
        Assert.Equal(["novo_orcamento", "funcionario"], result.AnsweredDialogs.ToArray());
    }

    [Fact]
    public async Task A_field_that_stays_blocked_with_no_notice_to_answer_stops()
    {
        var program = new FakeProgram { AlwaysBlocked = $"set {Volume}=60" };

        var result = await Run(program);

        Assert.Equal(PlaybookStopReason.ControlBlocked, result.Reason);
        Assert.Equal(2, program.Actions.Count(action => action == $"set {Volume}=60"));
    }

    [Fact]
    public async Task A_row_with_an_empty_cell_stops_before_anything_of_that_row_is_typed()
    {
        // Enter on an empty grid row saves the quote in FórmulaCerta: it must never be sent.
        var program = new FakeProgram();
        var data = Data(("7995", " ", "MG"));

        var result = await Run(program, data);

        Assert.Equal(PlaybookStopReason.EmptyCell, result.Reason);
        Assert.Equal("componentes[1]", result.StepId);
        Assert.DoesNotContain($"type {Grid}=7995", program.Actions);
    }

    [Fact]
    public async Task Missing_data_stops_naming_the_value()
    {
        var data = Data() with { Values = ImmutableDictionary<string, string>.Empty.Add("volume", "60") };

        var result = await Run(new FakeProgram(), data);

        Assert.Equal(PlaybookStopReason.MissingData, result.Reason);
        Assert.Equal("unidade", result.Detail);
    }

    [Fact]
    public async Task A_rule_whose_button_the_dialog_does_not_have_stops()
    {
        var program = new FakeProgram();
        program.DialogAfter($"type {Grid}=MG", new OpenDialog(8, "TMessageForm", "Atenção!", ["Sim", "Não"]), "Produto abaixo do Estoque Mínimo!");

        Assert.Equal(PlaybookStopReason.DialogButtonMissing, (await Run(program)).Reason);
    }

    [Fact]
    public async Task A_click_on_a_forbidden_button_stops_without_clicking()
    {
        var program = new FakeProgram { Captions = { ["TPanel[2]/TButton[0]"] = "&Imprimir" } };
        var playbook = Playbook() with { Steps = [new PlaybookStep("ok", PlaybookStepKind.Click, "TPanel[2]/TButton[0]")] };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(playbook, Data(), CancellationToken.None);

        Assert.Equal(PlaybookStopReason.ForbiddenButton, result.Reason);
        Assert.DoesNotContain(program.Actions, action => action.StartsWith("click", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_window_not_being_open_stops_at_once()
    {
        var program = new FakeProgram { WindowOpen = false };

        var result = await Run(program);

        Assert.Equal(PlaybookStopReason.WindowNotFound, result.Reason);
        Assert.Empty(program.Actions);
    }

    [Fact]
    public async Task Cancelling_stops_between_actions()
    {
        using var cancellation = new CancellationTokenSource();
        var program = new FakeProgram();
        program.OnAction = action =>
        {
            if (action == $"set {Unit}=CAP")
            {
                cancellation.Cancel();
            }
        };

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(Playbook(), Data(), cancellation.Token);

        Assert.Equal(PlaybookStopReason.Cancelled, result.Reason);
        Assert.DoesNotContain(program.Actions, action => action.StartsWith("type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dialog_that_keeps_coming_back_stops_instead_of_looping()
    {
        var program = new FakeProgram { DialogStaysAfterPress = true };
        program.DialogAfter($"type {Grid}=MG", new OpenDialog(8, "TMessageForm", "Atenção!", ["OK"]), "Produto abaixo do Estoque Mínimo!");

        Assert.Equal(PlaybookStopReason.TooManyDialogs, (await Run(program)).Reason);
    }

    [Fact]
    public async Task An_invalid_playbook_does_not_run()
    {
        var program = new FakeProgram();

        var result = await new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(Playbook() with { ProcessIdentity = "" }, Data(), CancellationToken.None);

        Assert.Equal(PlaybookStopReason.InvalidPlaybook, result.Reason);
        Assert.Empty(program.Actions);
    }

    private static Task<PlaybookRunResult> Run(FakeProgram program, PlaybookData? data = null) =>
        new PlaybookRunner(program, PlaybookRunnerOptions.Immediate).RunAsync(Playbook(), data ?? Data(), CancellationToken.None);

    private static Playbook Playbook() => new(
        "lancar_formula",
        "Lançar fórmula",
        "fcreceitas",
        "Orçamentos",
        [
            new PlaybookStep("volume", PlaybookStepKind.SetField, Volume, "{volume}"),
            new PlaybookStep("unidade", PlaybookStepKind.SetField, Unit, "{unidade}"),
            new PlaybookStep("componentes", PlaybookStepKind.GridRows, Grid, Rows: "componentes", Cells: ["{codigo}", "{quantidade}", "{unidade}"]),
        ],
        [
            new DialogRule("funcionario", "TfrFuncionario", "Funcionário Recepção", Response: DialogResponse.FillAndPress, Button: "OK", Field: "TEdit[1]", Value: "{funcionario}"),
            new DialogRule("estoque_minimo", "TMessageForm", "Atenção", "Estoque Mínimo", DialogResponse.Press, "OK"),
            new DialogRule("quantidade_zerada", "TfrAtencao", "Atenção", "Quantidade zerada", DialogResponse.Stop),
        ]);

    private static PlaybookData Data(params (string Code, string Quantity, string Unit)[] extra)
    {
        var rows = new[] { ("25531", "75", "MG") }.Concat(extra.Length == 0 ? [("7995", "5", "MG")] : extra)
            .Select(row => ImmutableDictionary<string, string>.Empty.Add("codigo", row.Item1).Add("quantidade", row.Item2).Add("unidade", row.Item3))
            .ToImmutableArray();
        return new PlaybookData(
            ImmutableDictionary<string, string>.Empty.Add("volume", "60").Add("unidade", "CAP").Add("funcionario", "19").Add("mais_formulas", "Não"),
            ImmutableDictionary<string, ImmutableArray<ImmutableDictionary<string, string>>>.Empty.Add("componentes", rows));
    }

    /// <summary>A program: fields keep what is typed (or a forced read-back), dialogs appear after given actions.</summary>
    private sealed class FakeProgram : IPlaybookDriver
    {
        private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (OpenDialog Dialog, string? Text)> _after = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OpenDialog> _blockOnce = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<OpenDialog>> _blockWith = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (OpenDialog Dialog, string Text)> _interruptOnce = new(StringComparer.Ordinal);
        private readonly Dictionary<long, string?> _texts = [];

        public bool WindowOpen { get; init; } = true;

        public int WindowOpensAfterAttempts { get; set; }

        public Dictionary<string, string> DialogFields { get; } = new(StringComparer.Ordinal);

        public bool DialogStaysAfterPress { get; init; }

        public Dictionary<string, string> ReadBack { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Captions { get; } = new(StringComparer.Ordinal);

        public List<string> Actions { get; } = [];

        public List<OpenDialog> Open { get; } = [];

        public Action<string>? OnAction { get; set; }

        public void DialogAfter(string action, OpenDialog dialog, string? text = "") => _after[action] = (dialog, text);

        public void BlockOnce(string action, OpenDialog dialog) => _blockOnce[action] = dialog;

        /// <summary>The action is blocked once per dialog, each dialog shown in turn.</summary>
        public void BlockWith(string action, params OpenDialog[] dialogs) => _blockWith[action] = new Queue<OpenDialog>(dialogs);

        public string? AlwaysBlocked { get; init; }

        public void InterruptOnce(string action, OpenDialog dialog, string text) => _interruptOnce[action] = (dialog, text);

        public Task<bool> AttachAsync(string processIdentity, string windowRule, CancellationToken cancellationToken) =>
            Task.FromResult(WindowOpen && WindowOpensAfterAttempts-- <= 0);

        public Task<string?> ReadDialogFieldAsync(OpenDialog dialog, string path, CancellationToken cancellationToken) =>
            Task.FromResult(DialogFields.GetValueOrDefault(path));

        public Task<WriteOutcome> SetTextAsync(string path, string text, CancellationToken cancellationToken)
        {
            _fields[path] = text;
            return Task.FromResult(Act($"set {path}={text}"));
        }

        public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(ReadBack.TryGetValue(path, out var forced) ? forced : _fields.GetValueOrDefault(path));

        public Task<WriteOutcome> TypeAsync(string path, string text, CancellationToken cancellationToken) => Task.FromResult(Act($"type {path}={text}"));

        public Task<WriteOutcome> PressKeyAsync(string path, PlaybookKey key, CancellationToken cancellationToken) => Task.FromResult(Act($"key {path}={key}"));

        public Task<WriteOutcome> PressAcceleratorAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Act($"accel {key}"));

        public Task<string?> CaptionAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Captions.GetValueOrDefault(path));

        public Task<WriteOutcome> ClickAsync(string path, CancellationToken cancellationToken) => Task.FromResult(Act($"click {path}"));

        public Task<WriteOutcome> ClickPointAsync(string path, int x, int y, CancellationToken cancellationToken) => Task.FromResult(Act($"clickpoint {path}@{x},{y}"));

        public Task<IReadOnlyList<OpenDialog>> DialogsAsync(IEnumerable<string> ignoredClasses, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OpenDialog>>(Open.ToArray());

        public Task<string?> DialogTextAsync(OpenDialog dialog, CancellationToken cancellationToken) => Task.FromResult(_texts.GetValueOrDefault(dialog.Handle));

        public Task<bool> PressDialogButtonAsync(OpenDialog dialog, string caption, CancellationToken cancellationToken)
        {
            Actions.Add($"press {dialog.Handle}={caption}");
            if (!DialogStaysAfterPress)
            {
                Open.RemoveAll(open => open.Handle == dialog.Handle);
            }

            return Task.FromResult(true);
        }

        public Task<bool> SetDialogTextAsync(OpenDialog dialog, string path, string text, CancellationToken cancellationToken)
        {
            Actions.Add($"fill {dialog.Handle} {path}={text}");
            return Task.FromResult(true);
        }

        private WriteOutcome Act(string action)
        {
            Actions.Add(action);
            OnAction?.Invoke(action);
            if (_blockOnce.Remove(action, out var blocking))
            {
                Show(blocking, string.Empty);
                return WriteOutcome.Blocked;
            }

            if (_blockWith.TryGetValue(action, out var queue) && queue.Count > 0)
            {
                Show(queue.Dequeue(), string.Empty);
                return WriteOutcome.Blocked;
            }

            if (action == AlwaysBlocked)
            {
                return WriteOutcome.Blocked;
            }

            if (_interruptOnce.Remove(action, out var interrupting))
            {
                Show(interrupting.Dialog, interrupting.Text);
                return WriteOutcome.Interrupted;
            }

            if (_after.Remove(action, out var shown))
            {
                Show(shown.Dialog, shown.Text);
            }

            return WriteOutcome.Done;
        }

        private void Show(OpenDialog dialog, string? text)
        {
            Open.Add(dialog);
            _texts[dialog.Handle] = text;
        }
    }
}
