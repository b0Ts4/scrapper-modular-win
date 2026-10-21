using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Prescriva.Agent.Application.Jobs;
using Prescriva.Agent.Application.Security;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Jobs;

/// <summary>The robot's settings as a JSON file (plan 22); the tokens are DPAPI-protected (base64), never in clear text.</summary>
public sealed class JsonRobotConfigurationStore(string path, IPayloadProtector protector) : IRobotConfigurationStore
{
    public async Task<RobotConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new RobotConfiguration(false, RobotLevel.Assisted, string.Empty, RobotConfiguration.DefaultLocalApiPort, NewToken(), null, null);
        }

        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync<RobotFile>(stream, JsonConfigurationStore.SharedJsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Robot settings are empty.");
        return new RobotConfiguration(file.Enabled, file.Level, file.EmployeeCode, file.LocalApiPort,
            Unprotect(file.LocalApiToken) ?? NewToken(), file.CrmUrl, Unprotect(file.CrmToken),
            string.IsNullOrWhiteSpace(file.QuotePlaybookId) ? RobotConfiguration.DefaultQuotePlaybook : file.QuotePlaybookId);
    }

    public async Task SaveAsync(RobotConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var file = new RobotFile(configuration.Enabled, configuration.Level, configuration.EmployeeCode, configuration.LocalApiPort,
            Protect(configuration.LocalApiToken)!, configuration.CrmUrl, Protect(configuration.CrmToken), configuration.QuotePlaybookId);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, file, JsonConfigurationStore.SharedJsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string? Protect(string? secret) =>
        string.IsNullOrEmpty(secret) ? null : Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(secret)));

    private string? Unprotect(string? protectedSecret) =>
        string.IsNullOrEmpty(protectedSecret) ? null : Encoding.UTF8.GetString(protector.Unprotect(Convert.FromBase64String(protectedSecret)));

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private sealed record RobotFile(bool Enabled, RobotLevel Level, string EmployeeCode, int LocalApiPort, string LocalApiToken, string? CrmUrl, string? CrmToken, string? QuotePlaybookId = null);
}
