# Current state

Foundation plan: `docs/superpowers/plans/2026-09-27-foundation-domain-and-configuration.md`.

Foundation Tasks 1–4 are implemented:

- .NET 10 Domain, Application and Infrastructure libraries, project boundaries, compiler conventions and contributor guidance.
- Immutable element fingerprints and deterministic selector scoring with process/window filters, confidence, evidence and explicit ambiguity. Public scoring weights are validated.
- Schema version 1 declarative configuration, six ordered action types, semantic ID/reference validation, and atomic JSON save/load. JSON mappings remain in Infrastructure; configuration contains no captured values.
- A pure immutable session engine with configured forward/back transitions, multiple triggers for one action, current-stage filtering, captured values and confirmed item snapshots. Required missing fields and typed capture failures reject the entire transition. Events carry caller IDs/time, configuration/session identity, type, payload and increasing sequence. Clear removes accumulated data; cancel clears and ends; finish emits `budget_finished` and ends. Runtime failures are typed and failed transitions preserve the original session.

Task 4 adds 27 real session test cases. The Release suite contains 53 Domain tests (17 selector, 9 configuration/boundary, 27 session) and 7 Infrastructure JSON tests; the Application test project remains empty. Verification commands:

```powershell
dotnet test tests/Prescriva.Agent.Domain.Tests --configuration Release --filter FullyQualifiedName~SessionEngineTests
dotnet test Prescriva.Agent.slnx --configuration Release
dotnet build Prescriva.Agent.slnx --configuration Release --no-restore
```

This environment's SDK executable is `C:\Users\arthu\AppData\Local\Microsoft\dotnet\dotnet.exe`; use that full path if the current shell does not have `dotnet` on PATH. See [testing](../testing.md) and the [session contract](../sessions.md).

Current plan: foundation implementation complete, pending final plan review/integration. Next plan: `docs/superpowers/plans/2026-09-27-windows-inspector-and-capture.md`, starting with Windows/Desktop/TestTarget projects and the application ports. Continue in the isolated worktree with the approved design and TDD workflow.

Limitations: window rules currently use normalized exact equality. Only configuration schema version 1 is supported. Session values are normalized strings supplied by callers; callers bind sessions to process instances/configurations and allocate globally unique IDs. The engine checks ID reuse within a session, not across sessions, and has no durable state or replay deduplication. It does not detect triggers or capture desktop data. The Windows Inspector, UI Automation capture, trigger runtime, SQLite/DPAPI event persistence, WPF UI, TestTarget and interactive manual checks are not implemented. The first milestone is therefore not complete.
