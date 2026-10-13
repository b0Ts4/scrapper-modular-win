using Prescriva.Agent.Application.Configuration;

namespace Prescriva.Agent.Application.Inspection;

/// <summary>
/// One open program window the person can choose to integrate with. Only what identifies the
/// window - never its content. Kept in memory for the chooser; never logged or saved.
/// </summary>
/// <param name="AppName">Friendly application name (file description, or the process name).</param>
/// <param name="WindowTitle">The window's title, as configured as the window rule.</param>
/// <param name="ProcessName">The process that owns the window's content (for Store apps, not the frame host).</param>
public sealed record OpenWindowInfo(string AppName, string WindowTitle, string ProcessName, int ProcessId);

/// <summary>Lists the visible, titled top-level windows of other programs.</summary>
public interface IOpenWindowSource
{
    Task<IReadOnlyList<OpenWindowInfo>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Search in the window chooser: by app name, title or process, ignoring accents and letter case.</summary>
public static class OpenWindowFilter
{
    public static IReadOnlyList<OpenWindowInfo> Apply(IEnumerable<OpenWindowInfo> windows, string? query)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var words = Normalize(query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return windows
            .Where(window => words.All(word =>
                Normalize(window.AppName).Contains(word, StringComparison.Ordinal) ||
                Normalize(window.WindowTitle).Contains(word, StringComparison.Ordinal) ||
                Normalize(window.ProcessName).Contains(word, StringComparison.Ordinal)))
            .OrderBy(window => window.AppName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.WindowTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string Normalize(string text) => SemanticIdGenerator.RemoveAccents(text).ToLowerInvariant();
}
