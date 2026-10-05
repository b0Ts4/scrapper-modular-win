# Current implementation plan

The first milestone is split into three sequential, independently reviewable plans:

1. `docs/superpowers/plans/2026-09-27-foundation-domain-and-configuration.md`
2. `docs/superpowers/plans/2026-09-27-windows-inspector-and-capture.md`
3. `docs/superpowers/plans/2026-09-27-trigger-runtime-events-and-test-mode.md`

Plan 1 (foundation) is complete: all 4 tasks implemented, task-reviewed, and the whole-branch final review passed (one fix wave closed its Important finding, re-reviewed clean).

Plan 2 (Windows Inspector and Capture) is complete: all 5 tasks implemented and task-reviewed, whole-branch final review passed after one fix wave (2 Critical, 2 Important — missing trigger UI, mouse-breaks-confirm, DPI-misplaced overlay, stale docs) closed clean. One item remains outside what any automated agent can verify: no human has yet physically clicked through the real `Prescriva.Agent.Desktop.exe` UI end-to-end. Current work: awaiting that manual check and/or the decision to start plan 3 (trigger runtime, events and test mode) in the same worktree/branch. See `docs/handoffs/current-state.md` for exact capabilities, verification and limitations. Execution method: subagent-driven development when the environment supports it.


Plan 3 (Trigger Runtime, Events and Test Mode) is implemented: Tasks 1–5, with the milestone end-to-end tests (`MilestoneFlowTests`, `DesktopWalkthroughTests`) passing on Windows CI (209/209 tests, 2026-10-04). Remaining for milestone sign-off: the person-driven visual walkthrough in `docs/testing/milestone-1-manual.md` on a Windows 10/11 desktop. See `docs/handoffs/current-state.md`.

Plan 4 — `docs/superpowers/plans/2026-10-04-configuration-lifecycle.md` (persisted approvals, editable configurations) — Tasks 1–5 implemented and verified on Windows CI (244 tests + 30 x86, 2026-10-04), including the `DesktopConfigurationLifecycleTests` walkthrough. Next: see `docs/roadmap.md` (selector resilience is the next candidate).

Plan 5 — `docs/superpowers/plans/2026-10-04-selector-resilience.md` (structural selector signals, weights v2, integration health) — Tasks 1–4 implemented and verified on Windows CI (263 tests + 30 x86, 2026-10-04). Next: see `docs/roadmap.md` (dispatcher recovery or transport are the next candidates).

Plan 6 — `docs/superpowers/plans/2026-10-05-file-and-image-fields.md` (file/image fields, encrypted attachments) — Tasks 1–5 implemented and verified on Windows CI (289 tests + 30 x86, 2026-10-05).

Plan 7 — `docs/superpowers/plans/2026-10-05-spec-gap-closure-and-startup.md` (content revision, bounded retry, test-mode evidence, tray, single instance, start with Windows) — Tasks 1–5 implemented; verified on Windows CI (see handoff). Next: OCR plan.

Plan 8 — `docs/superpowers/plans/2026-10-05-ocr-text-fields.md` (OCR text fields), plus the pending items closed in the same session (x86 hang root cause, dispatcher recovery, weights version on approvals, case-insensitive IDs, typed not-found) — see handoff.
