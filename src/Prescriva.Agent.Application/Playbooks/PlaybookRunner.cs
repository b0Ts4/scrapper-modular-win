using System.Collections.Immutable;
using System.Globalization;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Application.Playbooks;

/// <param name="SettleDelay">Time given to the program after every action before its dialogs are looked at.</param>
/// <param name="AttachTimeout">How long to wait for the program's window to open (Nova Fórmula opens Orçamentos a moment later).</param>
public sealed record PlaybookRunnerOptions(TimeSpan SettleDelay, int MaxDialogsPerAction = 10, TimeSpan AttachTimeout = default)
{
    // 20 (not 10): some products re-show a clinical advisory on each recálculo, so the save step can
    // answer more dialogs than a plain flow; still bounded, so a dialog that loops forever still stops.
    public static PlaybookRunnerOptions Default { get; } = new(TimeSpan.FromMilliseconds(700), MaxDialogsPerAction: 20, AttachTimeout: TimeSpan.FromSeconds(20));

    public static PlaybookRunnerOptions Immediate { get; } = new(TimeSpan.Zero);
}

public enum PlaybookRunStatus
{
    Completed,
    Stopped,
}

public enum PlaybookStopReason
{
    InvalidPlaybook,
    WindowNotFound,
    ControlNotFound,
    ControlBlocked,
    InputInterrupted,
    MissingData,
    AmbiguousNumber,
    EmptyCell,
    ValueMismatch,
    UnknownDialog,
    DialogRuleStop,
    DialogButtonMissing,
    ForbiddenButton,
    TooManyDialogs,
    Cancelled,
}

/// <summary>
/// How a run ended. <see cref="Detail"/> names a rule, a value's name or a dialog's class and
/// title - never a typed value, so it can be logged. <see cref="Captured"/> (values read from the
/// program) and <see cref="Unknown"/> (what an unknown dialog showed) are business data: they go
/// to the job's result and the pharmacy's notice list, never to technical logs.
/// </summary>
public sealed record PlaybookRunResult(
    PlaybookRunStatus Status,
    string? StepId = null,
    PlaybookStopReason? Reason = null,
    string? Detail = null,
    ImmutableArray<string> AnsweredDialogs = default,
    ImmutableDictionary<string, string>? Captured = null,
    UnknownDialog? Unknown = null)
{
    public ImmutableDictionary<string, string> Captured { get; init; } = Captured ?? ImmutableDictionary<string, string>.Empty;
}

/// <summary>A dialog the robot stopped on because no rule knows it: what it showed, for the pharmacy to decide on.</summary>
public sealed record UnknownDialog(string ClassName, string Title, ImmutableArray<string> Buttons, string? Text);

/// <summary>
/// Types a <see cref="Playbook"/> into its program (plan 19). After every action the program is
/// given time to settle, then every dialog it shows is answered by the first matching
/// <see cref="DialogRule"/>; a dialog no rule matches, a stop rule, a missing or forbidden button,
/// a value that does not read back as typed, or a grid row with an empty cell stops the run and
/// leaves the screen as it is for a person. It never presses OK/save by itself: a playbook ends
/// where its steps end.
/// </summary>
public sealed partial class PlaybookRunner(IPlaybookDriver driver, PlaybookRunnerOptions? options = null)
{
    private const int MaxBlockedTries = 4;

    private readonly PlaybookRunnerOptions _options = options ?? PlaybookRunnerOptions.Default;

    public async Task<PlaybookRunResult> RunAsync(Playbook playbook, PlaybookData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playbook);
        ArgumentNullException.ThrowIfNull(data);
        var run = new Run(playbook, data);
        try
        {
            if (PlaybookValidator.Validate(playbook).Count > 0)
            {
                return run.Stop(null, PlaybookStopReason.InvalidPlaybook);
            }

            if (!await AttachAsync(playbook, cancellationToken).ConfigureAwait(false))
            {
                return run.Stop(null, PlaybookStopReason.WindowNotFound);
            }

            foreach (var step in playbook.Steps.Where(step => step.RunsWith(data)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopped = step.Kind switch
                {
                    PlaybookStepKind.SetField => await SetFieldAsync(run, step, cancellationToken).ConfigureAwait(false),
                    PlaybookStepKind.GridRows => await GridRowsAsync(run, step, cancellationToken).ConfigureAwait(false),
                    PlaybookStepKind.ReadField => await ReadFieldAsync(run, step, cancellationToken).ConfigureAwait(false),
                    PlaybookStepKind.Accelerator => await AcceleratorAsync(run, step, cancellationToken).ConfigureAwait(false),
                    PlaybookStepKind.ClickPoint => await ClickPointAsync(run, step, cancellationToken).ConfigureAwait(false),
                    _ => await ClickAsync(run, step, cancellationToken).ConfigureAwait(false),
                };
                if (stopped is not null)
                {
                    return stopped;
                }
            }

            return run.Completed();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return run.Stop(run.CurrentStep, PlaybookStopReason.Cancelled);
        }
        catch (PlaybookDataException missing)
        {
            return run.Stop(run.CurrentStep, PlaybookStopReason.MissingData, missing.Name);
        }
    }

    private async Task<bool> AttachAsync(Playbook playbook, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.AttachTimeout;
        var openTried = false;
        while (!await driver.AttachAsync(playbook.ProcessIdentity, playbook.WindowRule, cancellationToken).ConfigureAwait(false))
        {
            // The window may close itself (FórmulaCerta's Orçamentos closes after each quote is saved):
            // when it is not open, open it once from another window of the program (Receitas, Alt+O).
            if (!openTried && playbook.OpenWith is { } opener &&
                await driver.AttachAsync(playbook.ProcessIdentity, opener.ViaWindowRule, cancellationToken).ConfigureAwait(false))
            {
                openTried = true;
                await driver.PressAcceleratorAsync(opener.Accelerator, cancellationToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(_options.AttachTimeout > TimeSpan.Zero ? 500 : 0), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(_options.AttachTimeout > TimeSpan.Zero ? 300 : 0), cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<PlaybookRunResult?> ReadFieldAsync(Run run, PlaybookStep step, CancellationToken cancellationToken)
    {
        run.CurrentStep = step.Id;
        var text = await driver.ReadTextAsync(step.Target, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return run.Stop(step.Id, PlaybookStopReason.ControlNotFound);
        }

        run.Captured[step.CaptureAs!] = text.Trim();
        return null;
    }

    private async Task<PlaybookRunResult?> AcceleratorAsync(Run run, PlaybookStep step, CancellationToken cancellationToken)
    {
        // Opens a menu/toolbar command (e.g. "Novo Orç."); the dialog it opens is answered by a rule.
        run.CurrentStep = step.Id;
        return await WriteAsync(run, step.Id, () => driver.PressAcceleratorAsync(step.Value!, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybookRunResult?> ClickPointAsync(Run run, PlaybookStep step, CancellationToken cancellationToken)
    {
        // Clicks a toolbar button that has no window handle (FórmulaCerta's "Novo Orç."); the dialog it opens is answered by a rule.
        run.CurrentStep = step.Id;
        PlaybookStep.TryParsePoint(step.Value, out var x, out var y); // the validator already refused any other value
        return await WriteAsync(run, step.Id, () => driver.ClickPointAsync(step.Target, x, y, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybookRunResult?> SetFieldAsync(Run run, PlaybookStep step, CancellationToken cancellationToken)
    {
        run.CurrentStep = step.Id;
        var value = PlaybookTemplate.Resolve(step.Value!, run.Data.Values);
        if (PointDecimal(value))
        {
            return run.Stop(step.Id, PlaybookStopReason.AmbiguousNumber);
        }

        var stopped = await WriteAsync(run, step.Id, () => driver.SetTextAsync(step.Target, value, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (stopped is not null)
        {
            return stopped;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var shown = await driver.ReadTextAsync(step.Target, cancellationToken).ConfigureAwait(false);
        if (shown is null)
        {
            return run.Stop(step.Id, PlaybookStopReason.ControlNotFound);
        }

        return SameValue(value, shown) ? null : run.Stop(step.Id, PlaybookStopReason.ValueMismatch);
    }

    private async Task<PlaybookRunResult?> GridRowsAsync(Run run, PlaybookStep step, CancellationToken cancellationToken)
    {
        if (!run.Data.Lists.TryGetValue(step.Rows!, out var rows))
        {
            run.CurrentStep = step.Id;
            throw new PlaybookDataException(step.Rows!);
        }

        for (var index = 0; index < rows.Length; index++)
        {
            var stepId = string.Create(CultureInfo.InvariantCulture, $"{step.Id}[{index}]");
            run.CurrentStep = stepId;
            var values = run.Data.Values.SetItems(rows[index]);
            var cells = step.Cells.Select(cell => PlaybookTemplate.Resolve(cell, values)).ToArray();

            // Enter on an empty grid row makes FórmulaCerta save the quote: an empty cell stops
            // the run before anything of the row is typed.
            if (cells.Any(string.IsNullOrWhiteSpace))
            {
                return run.Stop(stepId, PlaybookStopReason.EmptyCell);
            }

            if (cells.Any(PointDecimal))
            {
                return run.Stop(stepId, PlaybookStopReason.AmbiguousNumber);
            }

            foreach (var cell in cells)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopped = await WriteAsync(run, stepId, () => driver.TypeAsync(step.Target, cell, cancellationToken), cancellationToken).ConfigureAwait(false)
                    ?? await WriteAsync(run, stepId, () => driver.PressKeyAsync(step.Target, PlaybookKey.Enter, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (stopped is not null)
                {
                    return stopped;
                }
            }
        }

        return null;
    }

    private async Task<PlaybookRunResult?> ClickAsync(Run run, PlaybookStep step, CancellationToken cancellationToken)
    {
        run.CurrentStep = step.Id;
        var caption = await driver.CaptionAsync(step.Target, cancellationToken).ConfigureAwait(false);
        if (caption is null)
        {
            return run.Stop(step.Id, PlaybookStopReason.ControlNotFound);
        }

        if (Playbook.IsForbidden(caption, run.Playbook.AllForbiddenButtons))
        {
            return run.Stop(step.Id, PlaybookStopReason.ForbiddenButton);
        }

        return await WriteAsync(run, step.Id, () => driver.ClickAsync(step.Target, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One input, then the dialogs it caused. Input held by dialogs is tried again after they are
    /// answered - several dialogs can come one after the other (Nova Fórmula: "novo orçamento", then
    /// "Funcionário Recepção") - but only while each blocked try got a dialog answered, and at most
    /// <see cref="MaxBlockedTries"/> times.
    /// </summary>
    private async Task<PlaybookRunResult?> WriteAsync(Run run, string stepId, Func<Task<WriteOutcome>> write, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var answeredBefore = run.Answered.Count;
            var outcome = await write().ConfigureAwait(false);
            if (outcome == WriteOutcome.ControlNotFound)
            {
                return run.Stop(stepId, PlaybookStopReason.ControlNotFound);
            }

            var stopped = await AnswerDialogsAsync(run, stepId, cancellationToken).ConfigureAwait(false);
            if (stopped is not null)
            {
                return stopped;
            }

            if (outcome == WriteOutcome.Done)
            {
                return null;
            }

            if (outcome == WriteOutcome.Interrupted)
            {
                // Part of the input may already be in the field: typing it again could double it.
                return run.Stop(stepId, PlaybookStopReason.InputInterrupted);
            }

            if (attempt + 1 >= MaxBlockedTries || (attempt > 0 && run.Answered.Count == answeredBefore))
            {
                return run.Stop(stepId, PlaybookStopReason.ControlBlocked);
            }
        }
    }

    private async Task<PlaybookRunResult?> AnswerDialogsAsync(Run run, string stepId, CancellationToken cancellationToken)
    {
        for (var answered = 0; ; answered++)
        {
            if (_options.SettleDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.SettleDelay, cancellationToken).ConfigureAwait(false);
            }

            var dialogs = await driver.DialogsAsync(run.Playbook.AllIgnoredWindowClasses, cancellationToken).ConfigureAwait(false);
            if (dialogs.Count == 0)
            {
                return null;
            }

            if (answered >= _options.MaxDialogsPerAction)
            {
                return run.Stop(stepId, PlaybookStopReason.TooManyDialogs);
            }

            var dialog = dialogs[0];
            var (rule, text) = await MatchAsync(run.Playbook, dialog, cancellationToken).ConfigureAwait(false);
            if (rule is null)
            {
                text ??= await driver.DialogTextAsync(dialog, cancellationToken).ConfigureAwait(false);
                return run.Stop(stepId, PlaybookStopReason.UnknownDialog, $"{dialog.ClassName} '{dialog.Title}'") with
                {
                    Unknown = new UnknownDialog(dialog.ClassName, dialog.Title, dialog.Buttons, text),
                };
            }

            if (!string.IsNullOrWhiteSpace(rule.CaptureTitleAs))
            {
                run.Captured[rule.CaptureTitleAs] = dialog.Title.Length > rule.TitleStartsWith.Length
                    ? dialog.Title[rule.TitleStartsWith.Length..].Trim()
                    : string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(rule.CaptureField))
            {
                var captured = await driver.ReadDialogFieldAsync(dialog, rule.CaptureField, cancellationToken).ConfigureAwait(false);
                if (captured is null)
                {
                    return run.Stop(stepId, PlaybookStopReason.ControlNotFound, rule.Id);
                }

                run.Captured[rule.CaptureAs!] = captured.Trim();
            }

            switch (rule.Response)
            {
                case DialogResponse.Stop:
                    return run.Stop(stepId, PlaybookStopReason.DialogRuleStop, rule.Id);
                case DialogResponse.FillAndPress:
                    var value = PlaybookTemplate.Resolve(rule.Value!, run.Data.Values);
                    if (!await driver.SetDialogTextAsync(dialog, rule.Field!, value, cancellationToken).ConfigureAwait(false))
                    {
                        return run.Stop(stepId, PlaybookStopReason.ControlNotFound, rule.Id);
                    }

                    break;
            }

            var wanted = PlaybookTemplate.Resolve(rule.Button!, run.Data.Values);
            var button = dialog.Buttons.FirstOrDefault(caption => Playbook.Plain(caption) == Playbook.Plain(wanted));
            if (button is null)
            {
                return run.Stop(stepId, PlaybookStopReason.DialogButtonMissing, rule.Id);
            }

            if (Playbook.IsForbidden(button, run.Playbook.AllForbiddenButtons))
            {
                return run.Stop(stepId, PlaybookStopReason.ForbiddenButton, rule.Id);
            }

            if (!await driver.PressDialogButtonAsync(dialog, button, cancellationToken).ConfigureAwait(false))
            {
                return run.Stop(stepId, PlaybookStopReason.DialogButtonMissing, rule.Id);
            }

            run.Answered.Add(rule.Id);
        }
    }

    /// <summary>The first rule for the dialog's class and title whose text (when it asks for one) is in the dialog, and the text when it was read.</summary>
    private async Task<(DialogRule? Rule, string? Text)> MatchAsync(Playbook playbook, OpenDialog dialog, CancellationToken cancellationToken)
    {
        string? text = null;
        var textRead = false;
        foreach (var rule in playbook.Dialogs)
        {
            if (!string.Equals(rule.ClassName, dialog.ClassName, StringComparison.Ordinal) ||
                !Playbook.Plain(dialog.Title).StartsWith(Playbook.Plain(rule.TitleStartsWith), StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.TextContains))
            {
                return (rule, text);
            }

            if (!textRead)
            {
                text = await driver.DialogTextAsync(dialog, cancellationToken).ConfigureAwait(false);
                textRead = true;
            }

            if (text is not null && Playbook.Plain(text).Contains(Playbook.Plain(rule.TextContains), StringComparison.Ordinal))
            {
                return (rule, text);
            }
        }

        return (null, text);
    }

    /// <summary>Equal as text (loosely) or, when both are numbers, as numbers - "60" and "60,00" are the same.</summary>
    public static bool SameValue(string typed, string shown)
    {
        if (Playbook.Plain(typed) == Playbook.Plain(shown))
        {
            return true;
        }

        return Number(typed) is { } left && Number(shown) is { } right && left == right;
    }

    /// <summary>
    /// A number written with a decimal point ("0.5", "1.000"): FórmulaCerta reads numbers the
    /// Brazilian way, so it would take "0.5" as 5. Data must write decimals with a comma.
    /// </summary>
    private static bool PointDecimal(string value) => PointDecimalPattern().IsMatch(value.Trim());

    [System.Text.RegularExpressions.GeneratedRegex(@"^[+-]?\d+\.\d+$")]
    private static partial System.Text.RegularExpressions.Regex PointDecimalPattern();

    private static decimal? Number(string text)
    {
        var brazilian = CultureInfo.GetCultureInfo("pt-BR");
        return decimal.TryParse(text.Trim(), NumberStyles.Number, brazilian, out var value) ? value : null;
    }

    private sealed class Run(Playbook playbook, PlaybookData data)
    {
        public Playbook Playbook { get; } = playbook;

        public PlaybookData Data { get; } = data;

        public string? CurrentStep { get; set; }

        public List<string> Answered { get; } = [];

        public Dictionary<string, string> Captured { get; } = new(StringComparer.Ordinal);

        public PlaybookRunResult Completed() => new(PlaybookRunStatus.Completed, AnsweredDialogs: [.. Answered], Captured: Captured.ToImmutableDictionary());

        public PlaybookRunResult Stop(string? stepId, PlaybookStopReason reason, string? detail = null) =>
            new(PlaybookRunStatus.Stopped, stepId, reason, detail, [.. Answered], Captured.ToImmutableDictionary());
    }
}
