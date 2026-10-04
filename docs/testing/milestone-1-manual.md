# Milestone 1 — acceptance walkthrough

Run on Windows 10 or 11 x64 with an interactive desktop, after:

```powershell
dotnet build Prescriva.Agent.slnx --configuration Release
```

Start `src\Prescriva.Agent.TestTarget\bin\Release\net10.0-windows\Prescriva.Agent.TestTarget.exe` and `src\Prescriva.Agent.Desktop\bin\Release\net10.0-windows\Prescriva.Agent.Desktop.exe`. Place the two windows side by side. Local data goes to `%LOCALAPPDATA%\Prescriva\Agent` (set `PRESCRIVA_AGENT_DATA` to use another directory).

Each step lists its automated coverage. "Automated (CI)" steps run on every push in `.github/workflows/ci.yml` on a hosted `windows-latest` runner; the remaining "human" checks are visual and need a person.

| # | Step | Expected | Coverage |
| --- | --- | --- | --- |
| 1 | Create integration (`Prescriva.Agent.TestTarget.exe`, window `Prescriva Agent Test Target`), add stage `budget`. | Status confirms each step. | Automated (CI): `DesktopWalkthroughTests` |
| 2 | Start Inspection; hover `Medicamento`. | A red outline follows the control; hover text shows `AutomationId='MedicationTextBox'`. The Agent's own window and the outline are never selected. | Automated (CI): hover text with the real OS cursor, and the overlay window asserted within 2 px of the control's bounds; screenshots reviewed (outline visible around the text box and the Add button). Overlay exclusion: `OverlayExclusionTests`. Rendering on a real Windows 10/11 display at non-100% scaling: **human**. |
| 3 | Confirm, set semantic ID `medication`, Add Field. Repeat for concentration and quantity. | Summary lists the three fields with `*` (required). | Automated (CI) |
| 4 | Hover `Add`, confirm; trigger `add_item`, capture `medication, concentration, quantity`, emit `item_added`, finally Nothing. Hover `Finish`, confirm; trigger `finish_budget`, no capture/emit, finally *Finish session*. | Summary shows `capture(...) > emit(item_added)` and `finish`. | Automated (CI) |
| 5 | Save, close and reopen the Agent, Reload. | Configuration reloads with 3 fields; JSON contains no typed values. | Automated (CI): `DesktopWalkthroughTests` (same process), `DesktopResilienceWalkthroughTests` (after restarting the Agent; approval must be earned again). |
| 6 | Activate before testing. | "Ativação recusada ..." — nothing is monitored. | Automated (CI) |
| 7 | Type a medicine in TestTarget, Prepare Test, Executar teste, press Add then Finish in TestTarget. | Each field "Encontrado", provider `uia`, confidence, "Valor lido"; both triggers "Detectado"; Approve becomes enabled. | Automated (CI) |
| 8 | Approve, Activate. | Status "Monitorando ..."; diagnostics show "Monitorando gatilho" for both triggers. | Automated (CI) |
| 9 | Enter two medicines, pressing Add after each; press Finish. | Events list: `#1 item_added`, `#2 item_added`, `#3 budget_finished — 2 item(ns)` with values. | Automated (CI) |
| 10 | Clear `Quantidade`, press Add (new session: restart TestTarget). | Diagnostic "Gatilho 'add_item' rejeitado: o campo obrigatório 'quantity' está vazio. Nenhum evento foi gerado." | Automated (CI): `DesktopResilienceWalkthroughTests`, plus session/coordinator unit tests. |
| 11 | Stop, close the Agent, inspect `events.db`. | Three events remain, in order; the payload column is ciphertext. | Automated (CI): `DesktopWalkthroughTests`, `MilestoneFlowTests` |
| 12 | Restart TestTarget with `--layout-variant alternate`, Prepare Test and run. | Fields still "Encontrado" after controls move. | Automated (CI): `DesktopResilienceWalkthroughTests`, `SelectorResolutionTests`. |
| 13 | Start TestTarget with `--layout-variant duplicate-controls`, run test. | `medication` "Ambíguo" with actionable text; Approve disabled. | Automated (CI): `DesktopResilienceWalkthroughTests` ("Seleção ambígua", Approve disabled), `SelectorResolutionTests`, `TestModeViewModelTests`. |
| 14 | Two TestTarget instances while active. | Each instance gets its own session and sequence. | `MultipleInstanceTests` (CI). |
| 15 | With two events pending and `PRESCRIVA_AGENT_OUTBOX_WARNING=2`, look at the monitor. | Orange alert "2 eventos pendentes..."; nothing is deleted. | Automated (CI): `DesktopResilienceWalkthroughTests`, `RuntimeMonitorViewModelTests`. |
| 16 | While monitoring, close TestTarget. | "sessão encerrada"; no trigger error. | Automated (CI): `DesktopResilienceWalkthroughTests`, `SessionCoordinatorTests`. |
| 17 | Stop, then *Clear Local Data...* and confirm. | Disabled while monitoring; afterwards the events list is empty, `events.db` holds no events, `technical.jsonl` is emptied, configurations remain. | Automated (CI): `DesktopResilienceWalkthroughTests`, `SqliteEventOutboxTests`, `StructuredTechnicalLogTests`. |
| 19 | Approve, restart the Agent, Reload, Activate. | "Aprovada em …"; monitoring starts without retesting. Make a field optional → "Alterada desde o último teste", Activate refused; make it required again → "Aprovada" again. | Automated (CI): `DesktopConfigurationLifecycleTests`, `ApprovalServiceTests`, `JsonApprovalStoreTests`. |
| 20 | Remove a field captured by a trigger; remove a trigger and save. | First is refused naming the trigger; second requires a new test before activation. | Automated (CI): `DesktopConfigurationLifecycleTests`, `IntegrationEditorViewModelTests`. |
| 21 | Select TestTarget's "Observações:" box (no AutomationId) as a field captured by Add; restart TestTarget with `--layout-variant alternate`. | Hover text shows `Label='Observações:'`; the value is captured before and after the layout change; monitor shows "Integração saudável". | Automated (CI): `DesktopStructuralSelectorWalkthroughTests`, `StructuralSelectorTests`. |
| 18 | Repeat rows 2–9 with a 32-bit target application. | Same results. | Automated (CI): x86 pass of the integration suite against a self-contained `win-x86` TestTarget (bitness asserted). |

## Recorded results

| Date | Environment | Result |
| --- | --- | --- |
| 2026-10-04 | GitHub Actions `windows-latest` (Windows Server, x64), .NET SDK from `global.json` | All automated rows pass (6 of 6 runs on the final commit) — see the CI runs listed in [current state](../handoffs/current-state.md). |
| 2026-10-04 | GitHub Actions `windows-latest`, x86 TestTarget pass | 30/30 pass (TestTarget process confirmed 32-bit). |
| — | Windows 10/11 desktop, by a person | **Not yet performed.** Only row 2's real-display rendering at non-100% DPI remains person-only; every other row is automated. A final look-over by a person is still recommended before sign-off. |
