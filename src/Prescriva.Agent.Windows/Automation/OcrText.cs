using System.Text.RegularExpressions;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>Pure rules of OCR fields: which recognizer language to use, and how recognized lines become a value.</summary>
public static partial class OcrText
{
    /// <summary>The language preferred for Brazilian prescriptions.</summary>
    public const string PreferredLanguage = "pt-BR";

    /// <summary>
    /// pt-BR when installed; otherwise any Portuguese; otherwise the first of the user's
    /// profile languages that has a recognizer; otherwise the first recognizer installed.
    /// Null when no recognizer is installed at all.
    /// </summary>
    public static string? ChooseLanguage(IReadOnlyList<string> available, IReadOnlyList<string> profileLanguages)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(profileLanguages);
        if (available.Count == 0)
        {
            return null;
        }

        return available.FirstOrDefault(tag => string.Equals(tag, PreferredLanguage, StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(tag => SameLanguage(tag, "pt"))
            ?? profileLanguages
                .Select(profile => available.FirstOrDefault(tag => string.Equals(tag, profile, StringComparison.OrdinalIgnoreCase))
                    ?? available.FirstOrDefault(tag => SameLanguage(tag, profile)))
                .FirstOrDefault(tag => tag is not null)
            ?? available[0];
    }

    /// <summary>Recognized lines become the value: each line trimmed with single spaces, empty lines dropped, lines kept apart by '\n'.</summary>
    public static string Normalize(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return string.Join('\n', lines
            .Select(line => Whitespace().Replace(line ?? string.Empty, " ").Trim())
            .Where(line => line.Length > 0));
    }

    /// <summary>A recognized line and where it sits in the image (left edge, top edge, height).</summary>
    public sealed record PlacedLine(string Text, double X, double Y, double Height);

    /// <summary>
    /// Recognized lines become rows: lines whose vertical centres are within half a line height
    /// of each other are one row (a grid's cells), read left to right and joined by " | ";
    /// rows go top to bottom, kept apart by '\n'. Plain text one line under another is unchanged.
    /// </summary>
    public static string Rows(IEnumerable<PlacedLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rows = new List<List<PlacedLine>>();
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).OrderBy(line => line.Y + line.Height / 2))
        {
            var centre = line.Y + line.Height / 2;
            var row = rows.LastOrDefault();
            if (row is not null)
            {
                var first = row[0];
                if (Math.Abs(centre - (first.Y + first.Height / 2)) <= Math.Max(line.Height, first.Height) / 2)
                {
                    row.Add(line);
                    continue;
                }
            }

            rows.Add([line]);
        }

        return Normalize(rows.Select(row => string.Join(" | ", row.OrderBy(cell => cell.X)
            .Select(cell => Whitespace().Replace(cell.Text, " ").Trim()))));
    }

    private static bool SameLanguage(string tag, string other) =>
        string.Equals(PrimaryLanguage(tag), PrimaryLanguage(other), StringComparison.OrdinalIgnoreCase);

    private static string PrimaryLanguage(string tag)
    {
        var separator = tag.IndexOf('-');
        return separator < 0 ? tag : tag[..separator];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
