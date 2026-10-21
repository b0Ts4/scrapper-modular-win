using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Prescriva.Agent.Domain.Playbooks;

/// <summary>
/// What the Agent types into a program's screen, in which order, and how it answers the
/// program's dialogs (plan 19). Declarative data like an integration configuration: window
/// paths, value templates and rules - never business values, which come with each run as
/// <see cref="PlaybookData"/>.
/// </summary>
public sealed record Playbook(
    string Id,
    string Name,
    string ProcessIdentity,
    string WindowRule,
    ImmutableArray<PlaybookStep> Steps,
    ImmutableArray<DialogRule> Dialogs,
    ImmutableArray<string> ForbiddenButtons = default,
    ImmutableArray<string> IgnoredWindowClasses = default)
{
    /// <summary>Buttons the Agent never presses, whatever a playbook says: printing, production, sales, payments, prices, deletion.</summary>
    public static readonly ImmutableArray<string> DefaultForbiddenButtons =
        ["Imprimir", "Requisição", "Finalizar", "Vender", "Venda", "Aprovar", "Excluir", "Apagar", "Estornar", "Alterar Preço", "Receber", "Pagar"];

    /// <summary>Windows of the program that are never dialogs: its hidden application window and tooltips.</summary>
    public static readonly ImmutableArray<string> DefaultIgnoredWindowClasses = ["TApplication", "THintWindow"];

    /// <summary>The playbook's own forbidden buttons plus the defaults.</summary>
    public IEnumerable<string> AllForbiddenButtons => DefaultForbiddenButtons.Concat(ForbiddenButtons.IsDefault ? [] : ForbiddenButtons);

    public IEnumerable<string> AllIgnoredWindowClasses => DefaultIgnoredWindowClasses.Concat(IgnoredWindowClasses.IsDefault ? [] : IgnoredWindowClasses);

    /// <summary>True when <paramref name="caption"/> starts with a forbidden word, ignoring accents, letter case and the '&amp;' accelerator.</summary>
    public static bool IsForbidden(string? caption, IEnumerable<string> forbidden)
    {
        var plain = Plain(caption);
        return plain.Length > 0 && forbidden.Any(word => Plain(word) is { Length: > 0 } candidate && plain.StartsWith(candidate, StringComparison.Ordinal));
    }

    /// <summary>Text compared loosely: without '&amp;', accents and surrounding spaces, upper case.</summary>
    public static string Plain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Replace("&", string.Empty, StringComparison.Ordinal).Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }
}

public enum PlaybookStepKind
{
    /// <summary>Type <see cref="PlaybookStep.Value"/> into the control at <see cref="PlaybookStep.Target"/>, leave it with Tab, read it back.</summary>
    SetField,

    /// <summary>For each row of the list <see cref="PlaybookStep.Rows"/>, type each cell of <see cref="PlaybookStep.Cells"/> into the grid, Enter after each.</summary>
    GridRows,

    /// <summary>Press the button at <see cref="PlaybookStep.Target"/> (never a forbidden one).</summary>
    Click,
}

/// <summary>One action of a playbook. <see cref="Target"/> is a window path below the program's window.</summary>
public sealed record PlaybookStep(
    string Id,
    PlaybookStepKind Kind,
    string Target,
    string? Value = null,
    string? Rows = null,
    ImmutableArray<string> Cells = default);

public enum DialogResponse
{
    /// <summary>Press <see cref="DialogRule.Button"/>.</summary>
    Press,

    /// <summary>Type <see cref="DialogRule.Value"/> into <see cref="DialogRule.Field"/> (a path inside the dialog), then press <see cref="DialogRule.Button"/>.</summary>
    FillAndPress,

    /// <summary>Stop the run and leave the dialog for a person.</summary>
    Stop,
}

/// <summary>
/// How to answer a dialog: it matches a top-level window of the program with class
/// <see cref="ClassName"/>, a title starting with <see cref="TitleStartsWith"/> and, when
/// <see cref="TextContains"/> is set, that text in the dialog (read from its image). A dialog no
/// rule matches always stops the run.
/// </summary>
public sealed record DialogRule(
    string Id,
    string ClassName,
    string TitleStartsWith,
    string? TextContains = null,
    DialogResponse Response = DialogResponse.Stop,
    string? Button = null,
    string? Field = null,
    string? Value = null,
    string? Note = null);

/// <summary>The values of one run: named values and named lists of rows (e.g. the components).</summary>
public sealed record PlaybookData(
    ImmutableDictionary<string, string> Values,
    ImmutableDictionary<string, ImmutableArray<ImmutableDictionary<string, string>>> Lists)
{
    public static PlaybookData Empty { get; } = new(
        ImmutableDictionary<string, string>.Empty,
        ImmutableDictionary<string, ImmutableArray<ImmutableDictionary<string, string>>>.Empty);
}

public sealed record PlaybookValidationError(string Code, string Path, string Message);
