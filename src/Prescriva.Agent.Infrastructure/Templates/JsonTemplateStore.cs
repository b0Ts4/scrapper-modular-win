using System.Text.Json;
using Prescriva.Agent.Application.Templates;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Templates;

/// <summary>
/// Templates as JSON files (plan 15): the ones shipped with the Agent (read-only directory next to
/// the executable) and the person's own (saved under the data directory). An own template with
/// the ID of a shipped one replaces it in the list. A file that is not a valid template is left
/// out rather than breaking the list. Templates hold positions and explanations, never values.
/// </summary>
public sealed class JsonTemplateStore : ITemplateStore
{
    private readonly string _builtInDirectory;
    private readonly string _ownDirectory;

    public JsonTemplateStore(string builtInDirectory, string ownDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(builtInDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownDirectory);
        _builtInDirectory = Path.GetFullPath(builtInDirectory);
        _ownDirectory = Path.GetFullPath(ownDirectory);
    }

    public async Task<IReadOnlyList<StoredTemplate>> ListAsync(CancellationToken cancellationToken)
    {
        var builtIn = await ReadAllAsync(_builtInDirectory, cancellationToken).ConfigureAwait(false);
        var own = await ReadAllAsync(_ownDirectory, cancellationToken).ConfigureAwait(false);
        var ownIds = own.Select(template => template.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return builtIn.Where(template => !ownIds.Contains(template.Id)).Select(template => new StoredTemplate(template, IsBuiltIn: true))
            .Concat(own.Select(template => new StoredTemplate(template, IsBuiltIn: false)))
            .ToArray();
    }

    public async Task SaveAsync(IntegrationTemplate template, CancellationToken cancellationToken)
    {
        var errors = TemplateValidator.Validate(template);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(template));
        }

        Directory.CreateDirectory(_ownDirectory);
        var path = Path.Combine(_ownDirectory, template.Id + ".json");
        var temporaryPath = Path.Combine(_ownDirectory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, template, JsonConfigurationStore.SharedJsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<List<IntegrationTemplate>> ReadAllAsync(string directory, CancellationToken cancellationToken)
    {
        var templates = new List<IntegrationTemplate>();
        if (!Directory.Exists(directory))
        {
            return templates;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var template = await JsonSerializer.DeserializeAsync<IntegrationTemplate>(stream, JsonConfigurationStore.SharedJsonOptions, cancellationToken).ConfigureAwait(false);
                if (template is not null && TemplateValidator.Validate(template).Count == 0)
                {
                    templates.Add(template);
                }
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException)
            {
                // Not a template (or unreadable): left out of the list.
            }
        }

        return templates;
    }
}
