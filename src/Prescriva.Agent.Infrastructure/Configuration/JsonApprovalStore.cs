using System.Text.Json;
using Prescriva.Agent.Application.Configuration;
using Prescriva.Agent.Domain.Configuration;

namespace Prescriva.Agent.Infrastructure.Configuration;

/// <summary>
/// Stores approvals as <c>&lt;configurationId&gt;.approval.json</c> (configuration ID, content
/// hash and approval time - never a captured value). A file that cannot be read, is
/// incomplete, or names another configuration loads as "no approval": a damaged file can
/// make the operator retest, but can never approve anything.
/// </summary>
public sealed class JsonApprovalStore : IApprovalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _directory;

    public JsonApprovalStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public async Task SaveAsync(ConfigurationApproval approval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var path = PathFor(approval.ConfigurationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approval.Fingerprint);

        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, approval, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<ConfigurationApproval?> LoadAsync(string configurationId, CancellationToken cancellationToken)
    {
        var path = PathFor(configurationId);
        if (!File.Exists(path))
        {
            return null;
        }

        ConfigurationApproval? approval;
        try
        {
            await using var stream = File.OpenRead(path);
            approval = await JsonSerializer.DeserializeAsync<ConfigurationApproval>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException)
        {
            return null;
        }

        if (approval is null ||
            string.IsNullOrWhiteSpace(approval.Fingerprint) ||
            approval.ApprovedAtUtc == default ||
            !string.Equals(approval.ConfigurationId, configurationId, StringComparison.Ordinal))
        {
            return null;
        }

        return approval;
    }

    public Task DeleteAsync(string configurationId, CancellationToken cancellationToken)
    {
        var path = PathFor(configurationId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string? configurationId)
    {
        // Same file-name rule as JsonConfigurationStore.
        if (string.IsNullOrWhiteSpace(configurationId) || configurationId is "." or ".." || configurationId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("Configuration ID cannot be used as a file name.", nameof(configurationId));
        }

        return Path.Combine(_directory, configurationId + ".approval.json");
    }
}
