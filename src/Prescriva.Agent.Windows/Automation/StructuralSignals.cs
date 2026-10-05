using System.Collections.Immutable;
using System.Windows.Automation;
using Prescriva.Agent.Domain.Selectors;

namespace Prescriva.Agent.Windows.Automation;

/// <summary>
/// Extracts the structural selector signals of spec §6 - nearby label, ancestor chain and
/// position relative to the window - from a live element. Used both when an element is
/// inspected (the saved fingerprint) and when candidates are resolved, so the two sides
/// always compute them identically. Must run on the <see cref="AutomationDispatcher"/> thread.
///
/// Only static interface text is ever read as a label (an explicit LabeledBy target, or
/// the nearest preceding sibling Text element); an input control's own value is never
/// treated as a label.
/// </summary>
internal static class StructuralSignals
{
    private const int MaxAncestors = 3;
    private const int MaxSiblingsToScan = 4;

    public static ImmutableArray<string> NearbyLabels(AutomationElement element)
    {
        try
        {
            if (element.GetCurrentPropertyValue(AutomationElement.LabeledByProperty) is AutomationElement labeledBy &&
                Text(labeledBy) is { } explicitLabel)
            {
                return [explicitLabel];
            }

            var walker = TreeWalker.ControlViewWalker;
            var sibling = walker.GetPreviousSibling(element);
            for (var scanned = 0; sibling is not null && scanned < MaxSiblingsToScan; scanned++)
            {
                if (sibling.Current.ControlType == ControlType.Text && Text(sibling) is { } label)
                {
                    return [label];
                }

                sibling = walker.GetPreviousSibling(sibling);
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return ImmutableArray<string>.Empty;
    }

    /// <summary>Up to three control-view ancestors, nearest first, below the top-level window.</summary>
    public static ImmutableArray<AncestorFingerprint> Ancestors(AutomationElement element)
    {
        var builder = ImmutableArray.CreateBuilder<AncestorFingerprint>();
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var parent = walker.GetParent(element);
            while (parent is not null && builder.Count < MaxAncestors)
            {
                var grandParent = walker.GetParent(parent);
                if (grandParent is null || System.Windows.Automation.Automation.Compare(grandParent, AutomationElement.RootElement))
                {
                    break; // parent is the top-level window: it is already the window rule.
                }

                var current = parent.Current;
                builder.Add(new AncestorFingerprint(
                    NullIfEmpty(current.AutomationId),
                    current.ControlType?.ProgrammaticName,
                    NullIfEmpty(current.Name)));
                parent = grandParent;
            }
        }
        catch (ElementNotAvailableException)
        {
        }

        return builder.ToImmutable();
    }

    /// <summary>The element's bounds as fractions of its window's bounds, or null when either is empty.</summary>
    public static RelativeBounds? Relative(System.Windows.Rect element, System.Windows.Rect window)
    {
        if (element.IsEmpty || window.IsEmpty || window.Width <= 0 || window.Height <= 0)
        {
            return null;
        }

        return new RelativeBounds(
            Math.Round((element.X - window.X) / window.Width, 4),
            Math.Round((element.Y - window.Y) / window.Height, 4),
            Math.Round(element.Width / window.Width, 4),
            Math.Round(element.Height / window.Height, 4));
    }

    private static string? Text(AutomationElement element)
    {
        var name = element.Current.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
