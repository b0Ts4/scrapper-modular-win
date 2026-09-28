# Contributing to Prescriva Agent

The [approved design](docs/superpowers/specs/2026-09-27-prescriva-agent-milestone-1-design.md) and the active [implementation plan](docs/plans/current-plan.md) govern this repository. Read both before changing behavior. Keep each task small enough to review and commit independently.

## Required workflow order

1. Use Superpowers brainstorming to clarify a new feature or behavior change, then write or update its plan before coding.
2. Work in an isolated git worktree for feature implementation. Check `docs/handoffs/current-state.md` for the next task and follow its plan and task brief using Superpowers subagent-driven-development when available, or executing-plans for inline execution.
3. For deterministic behavior, follow test-driven development: write a meaningful failing test (RED), implement the smallest change (GREEN), then refactor. Scaffolding and documentation do not need artificial tests.
4. Review the diff and request code review for completed implementation work. Resolve significant findings and rerun affected checks.
5. Run fresh verification before claiming completion or committing: build and relevant automated tests, plus the Windows manual checklist where desktop behavior is involved. Read the output and report failures accurately.
6. Update setup, testing, architecture/decision records, and `docs/handoffs/current-state.md` when behavior or project status changes. Commit with a clear task-scoped message; use the branch-finishing workflow for integration decisions.

Do not skip a failed test or assume a prior run covers later edits. Do not create placeholder tests that only mirror an implementation.

## Architecture

- `Domain` holds typed configuration, selectors, sessions, actions and events. It must remain independent of Windows, WPF, UI Automation, SQLite and DPAPI.
- `Application` coordinates use cases through interfaces and may reference `Domain`.
- `Infrastructure` implements persistence and other adapters and may reference `Application` and `Domain`.
- Later `Windows` and `Desktop` projects implement desktop inspection and UI; integrations remain declarative data rather than ERP-specific code.
- Runtime support for this milestone is Windows 10/11 x64. Domain and Application target `net10.0` and should remain testable without an interactive desktop.

## Privacy and data handling

- Operate explicitly and visibly. Capture only elements that a user configured and authorized. Do not add a keylogger, global clipboard reader, hidden monitoring, or indiscriminate desktop capture.
- Configuration JSON is versioned and contains selectors and workflow definitions, never captured business values.
- Keep technical logs separate from business data; never log captured values. Inspection results stay in memory unless the user chooses to save them.
- Store business event payloads in the local SQLite queue protected at rest with Windows DPAPI for the executing user. Retain acknowledged events for seven days; retain unacknowledged events until acknowledged, with a storage limit and visible alert. Provide explicit local-data cleanup.
- Do not emit a valid event when a required field is missing. Make capture and selector failures visible and typed; never silently choose an ambiguous selector.

## Commands

```powershell
dotnet --version
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release --no-build
```

The SDK version must start with `10.`. Treat compiler warnings as errors. See [testing](docs/testing.md) for focused tests and the later interactive Windows test suite.

## Definition of Done

For a task: the specified behavior and boundaries are implemented; behavioral changes have relevant tests that first fail for the missing behavior and then pass; the Release solution build has zero warnings; applicable automated and manual checks pass; privacy rules hold; documentation and the handoff reflect the result; and the diff has been reviewed and committed.

The first milestone is complete only when, on Windows 10/11 x64, the Agent detects the TestTarget, visually configures fields and buttons with meanings, stages and actions, saves and reloads configuration, resolves elements with diagnostics, captures configured fields on `Adicionar`, persists `item_added`, emits `budget_finished` on `Finalizar`, shows values/events/confidence/failures in test mode, and passes applicable build, automated tests and the documented manual walkthrough.
