using System.Collections.Immutable;
using System.Globalization;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Application.Playbooks;

public sealed record PlaybookRunnerOptions(TimeSpan SettleDelay, int MaxDialogsPerAction = 10)
{
    /// <summary>Time given to the program after every action before its dialogs are looked at.</summary>
    public static PlaybookRunnerOptions Default { get; } = new(TimeSpan.FromMilliseconds(700));

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
    MissingData,
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
/// title - never a typed value, so a result can be logged.
/// </summary>
public sealed record PlaybookRunResult(
    PlaybookRunStatus Status,
    string? StepId = null,
    PlaybookStopReason? Reason = null,
    string? Detail = null,
    ImmutableArray<string> AnsweredDialogs = default);

/// <summary>
/// Types a <see cref="Playbook"/> into its program (plan 19). After every action the program is
/// given time to settle, then every dialog it shows is answered by the first matching
/// <see cref="DialogRule"/>; a dialog no rule matches, a stop rule, a missing or forbidden button,
/// a value that does not read back as typed, or a grid row with an empty cell stops the run and
/// leaves the screen as it is for a person. It never presses OK/save by itself: a playbook ends
/// where its steps end.
/// </summary>
public sealed class PlaybookRunner(IPlaybookDriver driver, PlaybookRunnerOptions? options = null)
{
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

            if (!await driver.AttachAsync(playbook.ProcessIdentity, playbook.WindowRule, cancellationToken).ConfigureAwait(false))
            {
                return run.Stop(null, PlaybookStopReason.WindowNotFound);
            }

            foreach (var step in playbook.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stopped = step.Kind switch
                {
                    PlaybookStepKind.SetField => await SetFieldAsync(run, step, cancellationToken).ConfigureAwait(false),
                    PlaybookStepKind.GridRows => await GridRowsAsync(run, step, cancellationToken).ConfigureAwait(false),
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

    private async Task<PlaybookRunResult?> SetFieldAsync(Run run, PlaybookStep step, CancellationToken cancellationToken)
    {
        run.CurrentStep = step.Id;
        var value = PlaybookTemplate.Resolve(step.Value!, run.Data.Values);
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

    /// <summary>One input, then the dialogs it caused; input held by a dialog is retried once after the dialog is answered.</summary>
    private async Task<PlaybookRunResult?> WriteAsync(Run run, string stepId, Func<Task<WriteOutcome>> write, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
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

            if (attempt > 0)
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
            var rule = await MatchAsync(run.Playbook, dialog, cancellationToken).ConfigureAwait(false);
            if (rule is null)
            {
                return run.Stop(stepId, PlaybookStopReason.UnknownDialog, $"{dialog.ClassName} '{dialog.Title}'");
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

            var button = dialog.Buttons.FirstOrDefault(caption => Playbook.Plain(caption) == Playbook.Plain(rule.Button));
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

    /// <summary>The first rule for the dialog's class and title whose text (when it asks for one) is in the dialog.</summary>
    private async Task<DialogRule?> MatchAsync(Playbook playbook, OpenDialog dialog, CancellationToken cancellationToken)
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
                return rule;
            }

            if (!textRead)
            {
                text = await driver.DialogTextAsync(dialog, cancellationToken).ConfigureAwait(false);
                textRead = true;
            }

            if (text is not null && Playbook.Plain(text).Contains(Playbook.Plain(rule.TextContains), StringComparison.Ordinal))
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>Equal as text (loosely) or, when both are numbers, as numbers - "60" and "60,00" are the same.</summary>
    internal static bool SameValue(string typed, string shown)
    {
        if (Playbook.Plain(typed) == Playbook.Plain(shown))
        {
            return true;
        }

        return Number(typed) is { } left && Number(shown) is { } right && left == right;
    }

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

        public PlaybookRunResult Completed() => new(PlaybookRunStatus.Completed, AnsweredDialogs: [.. Answered]);

        public PlaybookRunResult Stop(string? stepId, PlaybookStopReason reason, string? detail = null) =>
            new(PlaybookRunStatus.Stopped, stepId, reason, detail, [.. Answered]);
    }
}
