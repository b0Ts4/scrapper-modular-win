# Roadmap

## Milestone 1 (current)

Configure, test, activate and capture against `Prescriva.Agent.TestTarget` with local encrypted events. Status and evidence: [current state](handoffs/current-state.md).

## Next candidates

1. ~~Persisted approvals and configuration editing~~ — shipped in the configuration lifecycle plan (`docs/superpowers/plans/2026-10-04-configuration-lifecycle.md`). Remaining configurator UX: list-based editing (select a row instead of typing IDs), editing a field's selector by re-inspection, stage reordering.
2. ~~Selector resilience~~ — shipped (`docs/superpowers/plans/2026-10-04-selector-resilience.md`): structural signals, weights v2, confidence relative to available signals, Degraded/Broken health. Remaining: labels above a field in another container, storing the weights version with each configuration.
3. **Configurator UX:** list-based selection for edits, re-inspect to replace a selector. (Tray icon shipped with start-with-Windows.)
4. **Dispatcher recovery:** recover the UIA STA thread after a wedged COM call (hung target process).
5. **OCR (next plan, requested 2026-10-05):** a field type "text via OCR" read with the offline Windows OCR (`Windows.Media.Ocr`) from the image of the configured control, with the same covered-window refusal; also OCR of captured images. Then MSAA/Win32 providers.
6. **Transport:** deliver outbox events to the backend with idempotent confirmation (`MarkConfirmedAsync`), driving the 7-day retention of confirmed events.
7. **Packaging:** signed installer/updater (also removes Smart App Control friction on developer machines).

## Shipped after milestone 1

- File and image fields (`docs/superpowers/plans/2026-10-05-file-and-image-fields.md`). Possible follow-ups: capturing every file of an attachment list, choosing a per-field size limit or accepted types, OCR of captured images.
- Spec gap closure and start with Windows (`docs/superpowers/plans/2026-10-05-spec-gap-closure-and-startup.md`): content revision in events, bounded retry of transient failures, test-mode evidence (signals, lead, duration, fragility warnings, trigger effects), tray icon, single instance, start with Windows resuming the approved integration left active.
