using System.Text.Json;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Playbooks;

/// <summary>
/// Playbooks as JSON files (plan 19), in the same JSON shape as configurations: camelCase,
/// enum names as text, unknown or repeated properties refused. A playbook is loaded only when
/// <see cref="PlaybookValidator"/> accepts it. Run data (values and lists) is read the same way.
/// </summary>
public static class JsonPlaybookStore
{
    public static async Task<Playbook> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Playbook? playbook;
        try
        {
            await using var stream = File.OpenRead(path);
            playbook = await JsonSerializer.DeserializeAsync<Playbook>(stream, JsonConfigurationStore.SharedJsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PlaybookFileException(["MALFORMED_JSON"]);
        }

        var errors = PlaybookValidator.Validate(playbook);
        if (errors.Count > 0)
        {
            throw new PlaybookFileException(errors.Select(error => error.Code).ToArray());
        }

        return playbook!;
    }

    /// <summary>The playbooks in <paramref name="directory"/> that load; the others are left out.</summary>
    public static async Task<IReadOnlyList<Playbook>> ListAsync(string directory, CancellationToken cancellationToken)
    {
        var playbooks = new List<Playbook>();
        if (!Directory.Exists(directory))
        {
            return playbooks;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                playbooks.Add(await LoadAsync(file, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is PlaybookFileException or IOException)
            {
            }
        }

        return playbooks;
    }

    public static PlaybookData ReadData(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<PlaybookData>(json, JsonConfigurationStore.SharedJsonOptions);
            return data is null || data.Values is null || data.Lists is null ? throw new PlaybookFileException(["MALFORMED_DATA"]) : data;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PlaybookFileException(["MALFORMED_DATA"]);
        }
    }
}

/// <summary>A playbook or data file that cannot be used; <see cref="Codes"/> say why (never values).</summary>
public sealed class PlaybookFileException(IReadOnlyList<string> codes) : Exception("Invalid playbook file: " + string.Join(", ", codes))
{
    public IReadOnlyList<string> Codes { get; } = codes;
}
