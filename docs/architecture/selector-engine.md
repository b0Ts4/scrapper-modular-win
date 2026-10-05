# Selector engine

**Model.** `ElementFingerprint` (Domain) records the signals of a confirmed element: process identity, window rule, `AutomationId`, `Name`, `ControlType`, `ClassName`, `FrameworkId`, up to three control-view ancestors below the window, the nearby label, and the position relative to the window.

**Structural signals.** `StructuralSignals` (Windows) extracts them identically for the inspected element and for resolution candidates:
- *label*: the element's `LabeledBy` target, else the nearest preceding sibling `Text` element (static interface text only — an input's own value is never a label);
- *ancestors*: up to three control-view parents below the top-level window (AutomationId, ControlType, Name);
- *relative bounds*: element bounds as fractions of the window bounds.
The resolver computes a structural signal for a candidate only when the selector carries it and the candidate has the selector's control type.

**Matching.** `SelectorMatcher` (Domain, pure) filters candidates by process/window, scores each with `SelectorWeights` and returns `Found` (with confidence and evidence), `Ambiguous` (best score without a clear lead — never silently chosen) or `NotFound`.

| Weights v2 (`SelectorWeights.Version = 2`) | |
| --- | --- |
| AutomationId | 40 |
| NearbyLabels | 20 |
| ControlType | 15 |
| Name | 7 |
| Ancestors | 6 |
| ClassName | 5 |
| RelativeBounds | 5 |
| FrameworkId | 2 |
| Minimum score / minimum lead | 40 / 10 |

Consequences, each covered by `SelectorMatcherTests`: an `AutomationId` alone is a strong match; a control without `AutomationId` is found by its label plus type (also after it moves); two controls sharing a label are ambiguous; an unlabeled control without `AutomationId` is never guessed from type and structure; position is the weakest signal.

**Confidence** is the share of the selector's own signals that matched (`score ÷ total weight of the signals the selector carries`): a full match is 1.0 however few signals the selector has. A `Found` match below 0.8 raises `SelectorFallback` and degrades integration health.

**Resolution.** `UiAutomationSelectorResolver` locates the application window with `AutomationWindowLocator` (optionally scoped to one process ID), enumerates descendants on the `AutomationDispatcher` STA thread and returns an opaque `ResolvedElementHandle` for a `Found` match. `ProcessIdentity.ToProcessName` lets `Erp.exe` and `Erp` name the same process.

**Known limits.** Labels are detected only as `LabeledBy` or a preceding sibling `Text` (not labels above a field in a different container); the weights version is not yet stored with each configuration.

**Approvals and weights.** An approval records the `SelectorWeights.Version` it was tested under; when the weights change, existing approvals stop activating until the integration is tested again (approval files written before the version was recorded load as the current version).

**Dispatcher.** All UI Automation runs on one STA thread (`AutomationDispatcher`). A call still running after 30 s (a hung target application) is abandoned on the next call: queued work moves to a fresh STA thread, and the old thread ends when its stuck call returns.
