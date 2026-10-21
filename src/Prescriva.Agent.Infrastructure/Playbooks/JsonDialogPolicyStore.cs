using System.Collections.Immutable;
using System.Text.Json;
using Prescriva.Agent.Application.Playbooks;
using Prescriva.Agent.Domain.Playbooks;
using Prescriva.Agent.Infrastructure.Configuration;

namespace Prescriva.Agent.Infrastructure.Playbooks;

/// <summary>
/// One JSON file per playbook (<c>&lt;id&gt;.avisos.json</c>) in the Agent's local data (plan 20):
/// the pharmacy's dialog rules and the notices the robot met. A seen notice keeps the text read
/// from the screen, which may show patient data: the file stays local, never in Git or logs.
/// </summary>
public sealed class JsonDialogPolicyStore(string directory) : IDialogPolicyStore
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DialogPolicy?> LoadAsync(string playbookId, CancellationToken cancellationToken)
    {
        var file = await ReadAsync(playbookId, cancellationToken).ConfigureAwait(false);
        return file is null ? null : new DialogPolicy(file.PlaybookId, file.Rules.IsDefault ? [] : file.Rules);
    }

    public async Task<IReadOnlyList<SeenNotice>> SeenAsync(string playbookId, CancellationToken cancellationToken)
    {
        var file = await ReadAsync(playbookId, cancellationToken).ConfigureAwait(false);
        return file is null || file.Seen.IsDefault ? [] : file.Seen;
    }

    public async Task SaveAsync(DialogPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(policy.PlaybookId, cancellationToken).ConfigureAwait(false);
            await WriteAsync(new PolicyFile(policy.PlaybookId, policy.Rules.IsDefault ? [] : policy.Rules, current?.Seen.IsDefault == false ? current.Seen : []), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordSeenAsync(string playbookId, UnknownDialog dialog, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(playbookId, cancellationToken).ConfigureAwait(false) ?? new PolicyFile(playbookId, [], []);
            var seen = current.Seen.IsDefault ? [] : current.Seen;
            var titleStart = SeenNotice.TitleStart(dialog.Title);
            var existing = seen.FirstOrDefault(notice => notice.ClassName == dialog.ClassName && notice.TitleStartsWith == titleStart &&
                Playbook.Plain(notice.Text) == Playbook.Plain(dialog.Text));
            seen = existing is null
                ? seen.Add(new SeenNotice(dialog.ClassName, titleStart, dialog.Text, dialog.Buttons, DateTimeOffset.Now, 1))
                : seen.Replace(existing, existing with { LastSeen = DateTimeOffset.Now, Times = existing.Times + 1, Buttons = dialog.Buttons });
            await WriteAsync(current with { Seen = seen }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PolicyFile?> ReadAsync(string playbookId, CancellationToken cancellationToken)
    {
        var path = PathFor(playbookId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<PolicyFile>(stream, JsonConfigurationStore.SharedJsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new PlaybookFileException(["MALFORMED_JSON"]);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new PlaybookFileException(["MALFORMED_JSON"]);
        }
    }

    private async Task WriteAsync(PolicyFile file, CancellationToken cancellationToken)
    {
        var path = PathFor(file.PlaybookId);
        Directory.CreateDirectory(_directory);
        var temporary = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
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

    private string PathFor(string playbookId)
    {
        if (string.IsNullOrWhiteSpace(playbookId) || playbookId is "." or ".." ||
            playbookId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The playbook ID cannot be used as a file name.", nameof(playbookId));
        }

        return Path.Combine(_directory, playbookId + ".avisos.json");
    }

    private sealed record PolicyFile(string PlaybookId, ImmutableArray<DialogRule> Rules, ImmutableArray<SeenNotice> Seen);
}
