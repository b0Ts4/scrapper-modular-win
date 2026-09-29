# Current state

Foundation plan: `docs/superpowers/plans/2026-09-27-foundation-domain-and-configuration.md`.

Foundation Tasks 1–4 are implemented:

- .NET 10 Domain, Application and Infrastructure libraries, project boundaries, compiler conventions and contributor guidance.
- Immutable element fingerprints and deterministic selector scoring with process/window filters, confidence, evidence and explicit ambiguity. Public scoring weights are validated.
- Schema version 1 declarative configuration, six ordered action types, semantic ID/reference validation, and atomic JSON save/load. JSON mappings remain in Infrastructure; configuration contains no captured values.
- A pure immutable session engine with configured forward/back transitions, multiple triggers for one action, current-stage filtering, captured values and confirmed item snapshots. Required missing fields and typed capture failures reject the entire transition. Events carry caller IDs/time, configuration/session identity, type, payload and increasing sequence. Clear removes accumulated data; cancel clears and ends; finish emits `budget_finished` and ends. Runtime failures are typed and failed transitions preserve the original session.

Task 4 adds 27 real session test cases. A final-review fix wave then closed a gap where the emit-time required-field check covered only the current stage (letting `budget_finished`, or any other emit, fire while a required field belonging to a stage the session had left or never visited was missing); `Emit` now validates required fields across every configured stage, `SessionEngine.Apply` returns `SessionFailure(InvalidSession)` instead of throwing on a null occurrence/values argument or a malformed session, and two store tests were added (successful overwrite, default-vs-explicit-empty-array selector round trip). The Release suite now contains 58 Domain tests (17 selector, 9 configuration/boundary, 32 session) and 9 Infrastructure JSON tests; the Application test project remains empty. Verification commands:

```powershell
dotnet test tests/Prescriva.Agent.Domain.Tests --configuration Release --filter FullyQualifiedName~SessionEngineTests
dotnet test Prescriva.Agent.slnx --configuration Release
dotnet build Prescriva.Agent.slnx --configuration Release --no-restore
```

If this environment's shell does not have `dotnet` on PATH, locate the SDK executable (commonly under `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` on Windows) and use its full path for the commands above. See [testing](../testing.md) and the [session contract](../sessions.md).

Foundation plan is complete and merged into this branch (its own final whole-branch review passed, one fix wave applied and re-reviewed clean).

## Windows Inspector and Capture plan

Plan: `docs/superpowers/plans/2026-09-27-windows-inspector-and-capture.md`. Tasks 1–3 of 5 are complete (each task-reviewed, each with a fix round applied and re-reviewed clean):

- **Task 1**: `Prescriva.Agent.TestTarget` — a real, deterministic `net10.0-windows` WPF exe with all 12 required `AutomationProperties.AutomationId`s (`MedicationTextBox`, `ConcentrationTextBox`, `QuantityTextBox`, `FormComboBox`, `ItemsGrid`, `NextButton`, `BackButton`, `AddButton`, `FinishButton`, `CancelButton`, `DynamicField`, `ToggleDynamicFieldButton`), a working `--layout-variant` argument (moves controls, keeps IDs stable), Add-appends-a-row and Finish-shows-completion-state. `Prescriva.Agent.Windows` and `Prescriva.Agent.Desktop` scaffolded as empty `net10.0-windows`/WPF projects (no logic yet). `TestTargetLauncher` (test helper) launches/waits/kills a real TestTarget process for integration tests.
- **Task 2**: `AutomationDispatcher` — one dedicated background STA thread owns all real UI Automation access; every operation accepts cancellation and a timeout; queued work started after cancellation/timeout never runs its body. `UiAutomationElementInspector` implements `IElementInspector` (`FromPointAsync`, `FindCandidatesAsync`) against real UIA. `ElementSnapshot`/`ScreenPoint`/`InspectionResult` and the failure vocabulary `ElementInspectionFailure`/`ElementInspectionFailureKind` (`Cancelled`/`TimedOut`/`WindowMissing`/`ElementUnavailable`) live in `Prescriva.Agent.Application.Inspection` — plain, UIA-free data, so Application-layer code can branch on failure kind without a compile-time reference to `Prescriva.Agent.Windows`. Both `IElementInspector` methods map a `Cancelled` failure to `OperationCanceledException` identically.
- **Task 3**: `UiAutomationSelectorResolver` implements `ISelectorResolver.ResolveAsync` by combining Task 2's real candidate enumeration with the foundation plan's pure `SelectorMatcher` scoring, producing a `SelectorResolution` (exact match / moved-layout match / `Ambiguous` / not found / window missing). `UiAutomationCaptureProvider` implements `ICaptureProvider.CaptureAsync`, trying `ValuePattern` → `TextPattern` → `SelectionPattern` in order and recording every attempt; a liveness probe (`element.Current.ControlType`) distinguishes a genuinely destroyed element from one that simply lacks a pattern (`TryGetCurrentPattern` silently reports "unsupported" for a dead provider instead of throwing). `ResolvedElementHandle` is a genuinely opaque token (its concrete `UiaResolvedElementHandle` and the live `AutomationElement` it wraps are both `internal` to `Prescriva.Agent.Windows`, closing even a reflection-based extraction attempt). `AutomationWindowLocator.FindDescendants` is the single shared helper both the inspector and the resolver use for window-descendant enumeration + `WindowMissing` mapping (no duplication).

Verification commands (from the worktree root):

```powershell
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release
```

If `dotnet` isn't on PATH, locate the SDK executable (commonly `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` on Windows) and use its full path. As of Task 3's completion: Domain 58, Infrastructure 9, Windows.Tests 6, Windows.IntegrationTests 18 — all real tests (the integration tests launch and inspect a real `Prescriva.Agent.TestTarget.exe` process on an interactive desktop; they are not mocked and will not run headlessly). `Prescriva.Agent.Windows.IntegrationTests` disables test parallelization at the assembly level (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) because its tests drive real, visible desktop windows at fixed screen coordinates that would otherwise race each other.

Next: Task 4 (click-through overlay and selection controller — `InspectionController`, `HighlightOverlayWindow`, `InspectorViewModel`), then Task 5 (minimal configurator vertical slice + manual verification). Continue in this same worktree/branch with subagent-driven development.

Limitations (Windows Inspector plan, still open):
- The "element destroyed during capture" test scenario (Task 3) kills the whole TestTarget process rather than removing a single element from a still-running app/window — a real WPF/UIA limitation (`AutomationPeer.InvalidatePeer()` doesn't reliably produce `ElementNotAvailableException` since the CLR peer object stays alive) made the narrower scenario impractical to construct; accepted as a documented trade-off, not fixed.
- `FieldDefinition` is accepted by `ICaptureProvider.CaptureAsync` but not yet used for any per-field customization or selector/handle-consistency validation — capture behavior is currently identical regardless of the field passed in.
- `CaptureResult.Confidence` is currently always a hardcoded `1.0` (success) or `0` (failure), carrying no signal beyond the outcome already present.
- `FindApplicationWindow`/`AutomationWindowLocator.Find` enumerate every top-level desktop window and call `Process.GetProcessById` per window — O(all top-level windows) per call, unoptimized but not a correctness issue at current scale.

Limitations (foundation plan, still open — carried forward, see the original list below for full detail): `DomainEvent.ConfigurationVersion` conflates schema version with content revision; `SessionEngine`'s field-lookup relies on an upstream validator invariant; `SessionFailure.CaptureFailure` naming clash with its enum; untyped `FileNotFoundException` on missing-file load; case-sensitive ID uniqueness in `ConfigurationValidator`; `SelectorWeights` not persisted alongside saved configurations.

The Windows Inspector, UI Automation capture, trigger runtime, SQLite/DPAPI event persistence, WPF configurator UI and interactive manual checks are still incomplete overall — the first milestone is not yet complete (Tasks 4–5 of this plan, then the whole third plan, remain).

Additional still-open items, tracked only in this session's (gitignored) `.superpowers/` task notes until now:

- `DomainEvent.ConfigurationVersion` currently holds the configuration's schema version (always `1`), not a per-edit content revision, so two different revisions of the same configuration produce events that cannot be distinguished from each other. This must be resolved (rename the field, or add a real revision/hash) before a future plan persists events durably (e.g. to SQLite) and needs to reconstruct or deduplicate by exact configuration revision.
- `SessionEngine`'s field-lookup for a `CaptureFieldsAction`'s field ID assumes the referenced field always exists in `configuration.Fields` (guaranteed today only because the constructor validates the configuration first via `ConfigurationValidator`); if that invariant is ever broken by a future configuration-model change, the lookup will throw instead of producing a typed failure.
- `SessionFailure.CaptureFailure` (a record property) shares its name with the `CaptureFailure` enum type it holds, which reads awkwardly at call sites; a rename (e.g. to `CaptureFailureKind`) would be clearer but is cosmetic.
- The JSON configuration store's `LoadAsync` throws an untyped `FileNotFoundException` on a missing file rather than a typed "not found" error; a future plan that adds load/reload behavior should introduce a typed failure for this.
- `ConfigurationValidator`'s ID-uniqueness checks are case-sensitive, so two field/action IDs differing only in case are treated as distinct even though they may collide as string keys elsewhere; worth revisiting if this proves confusing in practice.
- `SelectorWeights` carries a `Version` in code, but nothing records which weights version resolved a selector or was in effect when a configuration was saved; a future plan may need to persist this alongside saved configurations for reproducibility.
