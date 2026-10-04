# Selector engine

**Model.** `ElementFingerprint` (Domain) records the stable signals of a confirmed element: process identity, window rule, `AutomationId`, `Name`, `ControlType`, `ClassName`, `FrameworkId` (ancestors/labels/relative position are declared but not populated yet).

**Matching.** `SelectorMatcher` (Domain, pure) filters candidates by process/window, scores each with `SelectorWeights`, and returns `Found` (with confidence and evidence), `Ambiguous` (best score without a clear lead — never silently chosen) or `NotFound`.

**Resolution.** `UiAutomationSelectorResolver` (Windows) locates the application window with `AutomationWindowLocator` — optionally scoped to one process ID so two running instances never cross — enumerates descendants on the `AutomationDispatcher` STA thread, and returns an opaque `ResolvedElementHandle` for a `Found` match. `WindowMissing`, `TimedOut`, `Ambiguous` and `NotFound` are typed statuses.

**Process identity.** `ProcessIdentity.ToProcessName` normalizes a configured identity, so `Erp.exe` and `Erp` both name the process `Erp` in discovery and window location.

**Known limits.** A control with no stable `AutomationId` usually scores below the ambiguity threshold and is reported `NotFound`; `SelectorWeights` versions are not persisted with configurations.
