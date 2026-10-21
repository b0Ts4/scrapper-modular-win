using System.Collections.Immutable;
using System.Text;

namespace Prescriva.Agent.Domain.Playbooks;

/// <summary>Value templates of a playbook: literal text with <c>{name}</c> placeholders.</summary>
public static class PlaybookTemplate
{
    public static bool IsValid(string? template)
    {
        if (template is null)
        {
            return false;
        }

        var open = -1;
        for (var index = 0; index < template.Length; index++)
        {
            switch (template[index])
            {
                case '{' when open >= 0:
                    return false;
                case '{':
                    open = index;
                    break;
                case '}' when open < 0 || index == open + 1 || string.IsNullOrWhiteSpace(template[(open + 1)..index]):
                    return false;
                case '}':
                    open = -1;
                    break;
            }
        }

        return open < 0;
    }

    public static IReadOnlyList<string> Names(string template)
    {
        if (!IsValid(template))
        {
            throw new ArgumentException("Invalid template.", nameof(template));
        }

        var names = new List<string>();
        var start = -1;
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == '{')
            {
                start = index + 1;
            }
            else if (template[index] == '}')
            {
                names.Add(template[start..index].Trim());
                start = -1;
            }
        }

        return names;
    }

    /// <summary>The template with every placeholder replaced; a placeholder without a value throws <see cref="PlaybookDataException"/>.</summary>
    public static string Resolve(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!IsValid(template))
        {
            throw new ArgumentException("Invalid template.", nameof(template));
        }

        var builder = new StringBuilder(template.Length);
        var start = -1;
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (character == '{')
            {
                start = index + 1;
            }
            else if (character == '}')
            {
                var name = template[start..index].Trim();
                if (!values.TryGetValue(name, out var value))
                {
                    throw new PlaybookDataException(name);
                }

                builder.Append(value);
                start = -1;
            }
            else if (start < 0)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}

/// <summary>A run's data lacks a value a template needs. Carries the placeholder's name only, never values.</summary>
public sealed class PlaybookDataException(string name) : Exception($"The data has no value '{name}'.")
{
    public string Name { get; } = name;
}
