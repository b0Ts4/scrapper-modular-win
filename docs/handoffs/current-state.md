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

Current plan: foundation implementation complete, pending final plan review/integration. Next plan: `docs/superpowers/plans/2026-09-27-windows-inspector-and-capture.md`, starting with Windows/Desktop/TestTarget projects and the application ports. Continue in the isolated worktree with the approved design and TDD workflow.

Limitations: window rules currently use normalized exact equality. Only configuration schema version 1 is supported. Session values are normalized strings supplied by callers; callers bind sessions to process instances/configurations and allocate globally unique IDs. The engine checks ID reuse within a session, not across sessions, and has no durable state or replay deduplication. It does not detect triggers or capture desktop data. The Windows Inspector, UI Automation capture, trigger runtime, SQLite/DPAPI event persistence, WPF UI, TestTarget and interactive manual checks are not implemented. The first milestone is therefore not complete.

Additional still-open items, tracked only in this session's (gitignored) `.superpowers/` task notes until now:

- `DomainEvent.ConfigurationVersion` currently holds the configuration's schema version (always `1`), not a per-edit content revision, so two different revisions of the same configuration produce events that cannot be distinguished from each other. This must be resolved (rename the field, or add a real revision/hash) before a future plan persists events durably (e.g. to SQLite) and needs to reconstruct or deduplicate by exact configuration revision.
- `SessionEngine`'s field-lookup for a `CaptureFieldsAction`'s field ID assumes the referenced field always exists in `configuration.Fields` (guaranteed today only because the constructor validates the configuration first via `ConfigurationValidator`); if that invariant is ever broken by a future configuration-model change, the lookup will throw instead of producing a typed failure.
- `SessionFailure.CaptureFailure` (a record property) shares its name with the `CaptureFailure` enum type it holds, which reads awkwardly at call sites; a rename (e.g. to `CaptureFailureKind`) would be clearer but is cosmetic.
- The JSON configuration store's `LoadAsync` throws an untyped `FileNotFoundException` on a missing file rather than a typed "not found" error; a future plan that adds load/reload behavior should introduce a typed failure for this.
- `ConfigurationValidator`'s ID-uniqueness checks are case-sensitive, so two field/action IDs differing only in case are treated as distinct even though they may collide as string keys elsewhere; worth revisiting if this proves confusing in practice.
- `SelectorWeights` carries a `Version` in code, but nothing records which weights version resolved a selector or was in effect when a configuration was saved; a future plan may need to persist this alongside saved configurations for reproducibility.
