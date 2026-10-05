using System.Text.Json;
using Prescriva.Agent.Application.Configuration;

namespace Prescriva.Agent.Infrastructure.Configuration;

/// <summary>
/// Stores the integration left monitoring in <c>monitoring.json</c> (a configuration ID only).
/// A missing, unreadable or empty file means "none": damage can stop automatic monitoring,
/// never start it.
/// </summary>
public sealed class JsonMonitoringPreferenceStore : IMonitoringPreferenceStore
{
    public const string FileName = "monitoring.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly string _path;

    public JsonMonitoringPreferenceStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, FileName);
    }

    public async Task<string?> GetActiveConfigurationIdAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var stored = await JsonSerializer.DeserializeAsync<Preference>(stream, JsonOptions, cancellationToken);
            return string.IsNullOrWhiteSpace(stored?.ActiveConfigurationId) ? null : stored.ActiveConfigurationId;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SetActiveConfigurationIdAsync(string? configurationId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new Preference(string.IsNullOrWhiteSpace(configurationId) ? null : configurationId), JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record Preference(string? ActiveConfigurationId);
}
