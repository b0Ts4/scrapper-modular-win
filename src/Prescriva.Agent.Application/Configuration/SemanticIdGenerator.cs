using System.Globalization;
using System.Text;

namespace Prescriva.Agent.Application.Configuration;

/// <summary>
/// Turns the friendly name a person types ("Concentração do remédio") into the internal ID the
/// configuration uses ("concentracao_do_remedio"): lowercase ASCII letters, digits and
/// underscores, starting with a letter, unique among <c>existing</c> ignoring letter case.
/// </summary>
public static class SemanticIdGenerator
{
    public static string FromName(string name, IEnumerable<string> existing, string fallbackPrefix = "campo")
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(existing);

        var builder = new StringBuilder();
        var pendingSeparator = false;
        foreach (var character in RemoveAccents(name).ToLowerInvariant())
        {
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (pendingSeparator && builder.Length > 0)
                {
                    builder.Append('_');
                }

                builder.Append(character);
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        var baseId = builder.Length == 0
            ? fallbackPrefix
            : char.IsAsciiLetter(builder[0]) ? builder.ToString() : fallbackPrefix + "_" + builder;

        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseId))
        {
            return baseId;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseId}_{suffix.ToString(CultureInfo.InvariantCulture)}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    internal static string RemoveAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character switch { 'ª' => 'a', 'º' => 'o', _ => character });
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
