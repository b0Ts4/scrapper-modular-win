namespace Prescriva.Agent.Domain.Configuration;

/// <summary>What a trigger watches (<see cref="TriggerDefinition.ObservedEvent"/>).</summary>
public static class TriggerEvents
{
    /// <summary>The element (a button) is clicked or invoked.</summary>
    public const string Invoke = "Invoke";

    /// <summary>
    /// A top-level window of the selector's program appears whose window class is the selector's
    /// <see cref="Selectors.ElementFingerprint.ClassName"/> and whose title starts with its
    /// <see cref="Selectors.ElementFingerprint.Name"/>, ignoring case (plan 17: e.g. a program's
    /// "more formulas?" question).
    /// </summary>
    public const string WindowShown = "WindowShown";
}
