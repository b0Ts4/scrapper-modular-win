using System.Collections.Immutable;
using Prescriva.Agent.Domain.Playbooks;

namespace Prescriva.Agent.Application.Playbooks;

/// <summary>
/// A pharmacy's answers to a playbook's notices and the notices the robot met and stopped on
/// (plan 20), kept in the Agent's local data.
/// </summary>
public interface IDialogPolicyStore
{
    Task<DialogPolicy?> LoadAsync(string playbookId, CancellationToken cancellationToken);

    /// <summary>Saves the pharmacy's rules; the notices seen are kept.</summary>
    Task SaveAsync(DialogPolicy policy, CancellationToken cancellationToken);

    Task<IReadOnlyList<SeenNotice>> SeenAsync(string playbookId, CancellationToken cancellationToken);

    /// <summary>Remembers a notice the robot stopped on; the same notice met again counts once more.</summary>
    Task RecordSeenAsync(string playbookId, UnknownDialog dialog, CancellationToken cancellationToken);
}

/// <summary>
/// A notice the robot met without a rule: its window class, the start of its title (without a
/// trailing number such as a quote's), the text read from it, its buttons, and how often.
/// </summary>
public sealed record SeenNotice(string ClassName, string TitleStartsWith, string? Text, ImmutableArray<string> Buttons, DateTimeOffset LastSeen, int Times)
{
    /// <summary>A title without its trailing number: "Orçamento 899124" is "Orçamento ".</summary>
    public static string TitleStart(string title) => title.TrimEnd().TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
}
