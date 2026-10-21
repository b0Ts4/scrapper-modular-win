using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Desktop.Playbooks;

/// <summary>
/// The pharmacy's choices for a playbook's notices (plan 20): one row per notice - the playbook's
/// own rules (merged with the pharmacy's) and the notices the robot met without a rule - each with
/// "stop and call a person" or "press «button»". Forbidden buttons are never offered; a notice the
/// playbook fills from the data (the employee code) or answers from the data (Q.S.P.) is shown but
/// cannot be changed here. <see cref="BuildPolicy"/> keeps only what differs from the playbook.
/// </summary>
public sealed class NoticeSettingsViewModel
{
    public const string StopChoice = "Parar e chamar uma pessoa";
    public const string FillChoice = "Preencher automaticamente";
    public const string FromDataChoice = "Responder conforme os dados";

    private readonly Playbook _playbook;

    public NoticeSettingsViewModel(Playbook playbook, DialogPolicy? policy, IReadOnlyList<SeenNotice> seen)
    {
        _playbook = playbook ?? throw new ArgumentNullException(nameof(playbook));
        var merged = playbook.WithPolicy(policy).Dialogs;
        var rows = new List<NoticeRow>();
        foreach (var rule in merged)
        {
            var buttons = new List<string>();
            if (!string.IsNullOrWhiteSpace(rule.Button) && !rule.Button.Contains('{', StringComparison.Ordinal))
            {
                buttons.Add(rule.Button);
            }

            buttons.AddRange(seen.Where(notice => Covers(rule, notice)).SelectMany(notice => notice.Buttons));
            rows.Add(new NoticeRow(rule, Title(rule), rule.TextContains, buttons, playbook, fromPolicy: policy?.Rules.Any(own => own.Id == rule.Id) == true, seen: null));
        }

        foreach (var notice in seen.Where(notice => !merged.Any(rule => Covers(rule, notice))))
        {
            rows.Add(new NoticeRow(null, notice.TitleStartsWith.Trim(), notice.Text, notice.Buttons, playbook, fromPolicy: false, notice));
        }

        Rows = rows;
    }

    public IReadOnlyList<NoticeRow> Rows { get; }

    /// <summary>The pharmacy's rules: changed playbook notices, its own earlier rules, and seen notices it chose to answer.</summary>
    public DialogPolicy BuildPolicy()
    {
        var rules = new List<DialogRule>();
        var ids = new HashSet<string>((_playbook.Dialogs.IsDefault ? [] : _playbook.Dialogs).Select(rule => rule.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows)
        {
            if (row.ToRule() is { } rule)
            {
                ids.Add(rule.Id);
                rules.Add(rule);
            }
        }

        foreach (var row in Rows.Where(row => row.NeedsNewRule))
        {
            var number = 1;
            while (ids.Contains(string.Create(CultureInfo.InvariantCulture, $"aviso_{number}")))
            {
                number++;
            }

            var id = string.Create(CultureInfo.InvariantCulture, $"aviso_{number}");
            ids.Add(id);
            rules.Add(row.NewRule(id));
        }

        return new DialogPolicy(_playbook.Id, [.. rules]);
    }

    private static string Title(DialogRule rule) =>
        string.IsNullOrWhiteSpace(rule.TextContains) ? rule.TitleStartsWith.Trim() : $"{rule.TitleStartsWith.Trim()} — {rule.TextContains}";

    /// <summary>True when the rule would answer this seen notice.</summary>
    private static bool Covers(DialogRule rule, SeenNotice notice) =>
        rule.ClassName == notice.ClassName &&
        Playbook.Plain(notice.TitleStartsWith).StartsWith(Playbook.Plain(rule.TitleStartsWith), StringComparison.Ordinal) &&
        (string.IsNullOrWhiteSpace(rule.TextContains) || Playbook.Plain(notice.Text).Contains(Playbook.Plain(rule.TextContains), StringComparison.Ordinal));

    /// <summary>The line of a notice's text read from the screen that best names it: the longest one.</summary>
    internal static string? LongestLine(string? text) =>
        text?.Split(['\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderByDescending(line => line.Length)
            .FirstOrDefault();
}

/// <summary>One notice and the choice for it.</summary>
public sealed class NoticeRow : INotifyPropertyChanged
{
    private readonly DialogRule? _rule;
    private readonly SeenNotice? _seen;
    private readonly bool _fromPolicy;
    private readonly string _initialChoice;
    private string _choice;

    internal NoticeRow(DialogRule? rule, string title, string? text, IEnumerable<string> buttons, Playbook playbook, bool fromPolicy, SeenNotice? seen)
    {
        _rule = rule;
        _seen = seen;
        _fromPolicy = fromPolicy;
        Title = title;
        Text = text;
        Origin = seen is not null
            ? string.Format(CultureInfo.GetCultureInfo("pt-BR"), "Visto {0} vez(es), a última em {1:dd/MM HH:mm}", seen.Times, seen.LastSeen)
            : fromPolicy ? "Escolha desta farmácia" : "Padrão do roteiro";

        CanChange = rule is null || (rule.Response != DialogResponse.FillAndPress && rule.Button?.Contains('{', StringComparison.Ordinal) != true);
        var offered = buttons.Where(button => !Playbook.IsForbidden(button, playbook.AllForbiddenButtons))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Press);
        Choices = CanChange ? [NoticeSettingsViewModel.StopChoice, .. offered] : [CurrentFixedChoice(rule!)];
        _choice = rule is null ? NoticeSettingsViewModel.StopChoice : ChoiceOf(rule);
        _initialChoice = _choice;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    /// <summary>The notice's text (read from the screen for a seen notice).</summary>
    public string? Text { get; }

    public string Origin { get; }

    public bool CanChange { get; }

    public IReadOnlyList<string> Choices { get; }

    public string Choice
    {
        get => _choice;
        set
        {
            if (!CanChange || !Choices.Contains(value) || value == _choice)
            {
                return;
            }

            _choice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Choice)));
        }
    }

    /// <summary>A seen notice the pharmacy chose to answer: it needs a new rule.</summary>
    internal bool NeedsNewRule => _rule is null && _choice != NoticeSettingsViewModel.StopChoice;

    /// <summary>The rule this row now stands for, when it must be in the pharmacy's policy.</summary>
    internal DialogRule? ToRule()
    {
        if (_rule is null)
        {
            return null;
        }

        // The pharmacy's earlier rules are kept; a playbook rule only when its choice was changed.
        return _fromPolicy || _choice != _initialChoice ? Apply(_rule) : null;
    }

    internal DialogRule NewRule(string id) => Apply(new DialogRule(
        id,
        _seen!.ClassName,
        _seen.TitleStartsWith,
        NoticeSettingsViewModel.LongestLine(_seen.Text),
        Note: "Escolhida pela farmácia para um aviso que o robô encontrou."));

    private DialogRule Apply(DialogRule rule)
    {
        if (!CanChange)
        {
            return rule;
        }

        return _choice == NoticeSettingsViewModel.StopChoice
            ? rule with { Response = DialogResponse.Stop, Button = null }
            : rule with { Response = DialogResponse.Press, Button = _choice[("Apertar «".Length)..^1] };
    }

    private static string Press(string button) => $"Apertar «{button}»";

    private static string ChoiceOf(DialogRule rule) => rule.Response switch
    {
        DialogResponse.Stop => NoticeSettingsViewModel.StopChoice,
        DialogResponse.FillAndPress => NoticeSettingsViewModel.FillChoice,
        _ when rule.Button?.Contains('{', StringComparison.Ordinal) == true => NoticeSettingsViewModel.FromDataChoice,
        _ => Press(rule.Button ?? string.Empty),
    };

    private static string CurrentFixedChoice(DialogRule rule) => ChoiceOf(rule);
}
