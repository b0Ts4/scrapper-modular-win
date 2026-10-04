# Roadmap

## Milestone 1 (current)

Configure, test, activate and capture against `Prescriva.Agent.TestTarget` with local encrypted events. Status and evidence: [current state](handoffs/current-state.md).

## Next candidates

1. **Operator data controls:** explicit "clear local data", visible capacity alert in the Desktop, persisted approvals (today approvals live in memory until the Agent restarts).
2. **Selector resilience:** populate ancestors, nearby labels and relative position so controls without a stable `AutomationId` can be resolved; persist the `SelectorWeights` version with configurations; `Degraded`/`Broken` integration states with visible reasons.
3. **Configurator UX:** remove/edit fields and triggers, per-field required/optional editing, stage list editing, tray icon.
4. **Dispatcher recovery:** recover the UIA STA thread after a wedged COM call (hung target process).
5. **Providers:** MSAA/Win32 providers, then OCR for applications without usable UI Automation.
6. **Transport:** deliver outbox events to the backend with idempotent confirmation (`MarkConfirmedAsync`), driving the 7-day retention of confirmed events.
7. **Packaging:** signed installer/updater (also removes Smart App Control friction on developer machines).
