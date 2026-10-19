namespace Prescriva.Agent.Domain.Configuration;

/// <summary>
/// How a window rule names a window (plan 18): the window's title equals it or contains it,
/// ignoring case - so a rule like "Orçamentos" keeps finding "FórmulaCerta - Orçamentos 6.0.1164"
/// after a program update changes the version. An exact title is preferred when both exist.
/// </summary>
public static class WindowRule
{
    public static bool IsExact(string? title, string? rule) =>
        title is not null && rule is not null && string.Equals(title.Trim(), rule.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool Matches(string? title, string? rule) =>
        string.IsNullOrWhiteSpace(rule) ||
        (title is not null && title.Contains(rule.Trim(), StringComparison.OrdinalIgnoreCase));
}
