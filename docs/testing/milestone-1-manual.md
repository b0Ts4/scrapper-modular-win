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
| 2 | Start Inspection; hover `Medicamento`. | A red outline follows the control; hover text shows `AutomationId='MedicationTextBox'`. The Agent's own window and the outline are never selected. | Hover text: automated (CI, real OS cursor). Outline drawn in the right place: **human**. Overlay exclusion: `OverlayExclusionTests` (CI). |
| 3 | Confirm, set semantic ID `medication`, Add Field. Repeat for concentration and quantity. | Summary lists the three fields with `*` (required). | Automated (CI) |
| 4 | Hover `Add`, confirm; trigger `add_item`, capture `medication, concentration, quantity`, emit `item_added`, finally Nothing. Hover `Finish`, confirm; trigger `finish_budget`, no capture/emit, finally *Finish session*. | Summary shows `capture(...) > emit(item_added)` and `finish`. | Automated (CI) |
| 5 | Save, close and reopen the Agent, Reload. | Configuration reloads with 3 fields; JSON contains no typed values. | Save/Reload in one process: automated (CI). **Across an Agent restart: human.** |
| 6 | Activate before testing. | "Ativação recusada ..." — nothing is monitored. | Automated (CI) |
| 7 | Type a medicine in TestTarget, Prepare Test, Executar teste, press Add then Finish in TestTarget. | Each field "Encontrado", provider `uia`, confidence, "Valor lido"; both triggers "Detectado"; Approve becomes enabled. | Automated (CI) |
| 8 | Approve, Activate. | Status "Monitorando ..."; diagnostics show "Monitorando gatilho" for both triggers. | Automated (CI) |
| 9 | Enter two medicines, pressing Add after each; press Finish. | Events list: `#1 item_added`, `#2 item_added`, `#3 budget_finished — 2 item(ns)` with values. | Automated (CI) |
| 10 | Clear `Quantidade`, press Add (new session: restart TestTarget). | Diagnostic "Gatilho 'add_item' rejeitado: o campo obrigatório 'quantity' está vazio. Nenhum evento foi gerado." | Session/coordinator unit tests (CI). Through the Desktop UI: **human**. |
| 11 | Stop, close the Agent, inspect `events.db`. | Three events remain, in order; the payload column is ciphertext. | Automated (CI): `DesktopWalkthroughTests`, `MilestoneFlowTests` |
| 12 | Restart TestTarget with `--layout-variant alternate`, Prepare Test and run. | Fields still "Encontrado" after controls move. | `SelectorResolutionTests` (CI). Through the Desktop: **human**. |
| 13 | Start TestTarget with `--layout-variant duplicate-controls`, run test. | `medication` "Ambíguo" with actionable text; Approve disabled. | `SelectorResolutionTests` + `TestModeViewModelTests` (CI). Through the Desktop: **human**. |
| 14 | Two TestTarget instances while active. | Each instance gets its own session and sequence. | `MultipleInstanceTests` (CI). |

## Recorded results

| Date | Environment | Result |
| --- | --- | --- |
| 2026-10-04 | GitHub Actions `windows-latest` (Windows Server, x64), .NET SDK from `global.json` | All automated rows pass (6 of 6 runs on the final commit) — see the CI runs listed in [current state](../handoffs/current-state.md). |
| — | Windows 10/11 desktop, by a person | **Not yet performed.** The rows marked *human* remain open. |
