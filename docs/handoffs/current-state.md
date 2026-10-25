# Current state

## Status — 2026-10-25 (atalho "Novo Orç." calibrado no FórmulaCerta real)

- **Calibração confirmada ao vivo** na janela "FórmulaCerta - Receitas 6.0.1164" (processo `FCReceitas.exe`): o novo orçamento é aberto pelo botão **Incluir = Alt+I**, que abre o diálogo "Requisição" (TfrPedeOrc) — exatamente a regra `novo_orcamento` do roteiro. Testado abrindo o diálogo e cancelando (nada gravado).
- **Roteiro atualizado** (`formulacerta_lancar_formula.json`): passo `abrir_orcamento` `value: "N" → "I"`; `windowRule: "Orçamentos" → "Receitas"` (o título real contém "Receitas", não "Orçamentos", senão o robô não acharia a janela).
- Build Release 0 warnings; testes unitários Domain/Application/Infrastructure/Windows verdes (527 no total). As 4 falhas do IntegrationTests são ambientais (MSIX/screenshot dir/walkthrough), sem relação.
- **CRM (receitaAI, `feat/crm-agent-demo`):** ambiente de teste no ar nesta máquina — CRM na :3000, túnel ngrok público, webhook da api-wa.me apontado, chaves OpenAI/wa.me carregadas no `.env`, `AUTO_ORCAMENTO=1`. Código do ativo agora cai no equivalente do catálogo quando não há match exato (`codigoSugerido`).

## Status — 2026-10-24 (demo CRM↔FórmulaCerta: o robô abre o orçamento sozinho; CRM integrado)

- **Item A (robô abre o orçamento novo):** novo passo de roteiro `accelerator` (Alt+<tecla>) que aciona um comando de menu/barra como "Novo Orç."; o diálogo que ele abre é respondido por uma regra (`novo_orcamento`). `QuoteRuns` marca `abrir=sim` só na 1ª fórmula do trabalho, então o robô abre o orçamento para trabalhos vindos do CRM; no fluxo de atendimento (abrir=não) nada muda. Testado na TestTarget (`win32-quote`, variante com Alt+N → "Requisição"). **A tecla exata do "Novo Orç." no FórmulaCerta ainda não foi confirmada** (a tela não estava aberta); o roteiro usa "N" como palpite, a confirmar no ensaio. Se a tecla estiver errada, o robô não abre e para em seguida, sem gravar.
- **CRM (repositório receitaAI, branch `feat/crm-agent-demo`):** botão "Gerar no FórmulaCerta" na Conferência; código do produto por ativo com busca no catálogo; monta o launch_quote do rascunho conferido; preenche o preço com o valor do FórmulaCerta. 50 testes do CRM + 29 da API/ponte OK. Demo em `crm/DEMO_FORMULACERTA.md`.
- **Falta para a demo hands-off:** confirmar a tecla do "Novo Orç." (1 caractere, no roteiro) e as chaves OpenAI/wa.me/AGENTE_TOKEN (o operador configura).
## Status — 2026-10-21 (milestone 2: plans 20 and 22; real save, MobyPharma and replies wait for the operator)

- **Plan 20** (`docs/superpowers/plans/2026-10-21-notices-and-saving.md`):
  - done: notices configurable per pharmacy (step 6 "Robô" → "Avisos do programa"), notices the robot met offered for a decision, captures, OK step;
  - **not done:** saving on the real FórmulaCerta. The session's safety check refused writing to the production system, so it waits for the operator's explicit authorization or a test database.
- **Plan 22** (`docs/superpowers/plans/2026-10-21-robot-jobs-and-connector.md`): done.
  - Encrypted job queue, local API (127.0.0.1 + token), CRM connector, robot loop and step 6 settings, with a banner and Parar.
  - Tested end to end on the TestTarget, by hand in the real Agent.
  - CRM side on branch `feature/agent-bridge` of `b0Ts4/receitaAI`; its Python tests were not run, because there is no Python here.
- **Plans 21 (attend a MobyPharma order) and 23 (reply in its chat): not started.** They need a test order, and authorization to take it and to send a message to a number the operator controls. MobyPharma shows live customer orders.
- **Left open in FórmulaCerta:** an empty, unsaved new quote with "Dados não gravados. Confirma cancelamento?". The safety check refused the "Sim" click.
## Status — 2026-10-20 (milestone 2 started: plan 19, the Agent fills FórmulaCerta's Orçamentos from data)

- **Design:** `docs/superpowers/specs/2026-10-20-milestone-2-automated-quotes-design.md`, approved by the operator ("pode implementar"). The Agent attends online orders in FórmulaCerta driven by the CRM (`b0Ts4/receitaAI`). There is no Fagron API, so it uses UI automation on a dedicated machine.
- **Operator decisions:**
  - no robot user: employee 19 is used and everything done in FórmulaCerta is logged in `.local/registro-de-testes.md` (gitignored, holds customer data);
  - the CRM side is built here too (plan 22);
  - the CRM keeps its own WhatsApp for other ERPs, but with FórmulaCerta the reply goes through MobyPharma.
- **Plan 19:** `docs/superpowers/plans/2026-10-20-playbook-write-actions.md`, done. See its Result section. The shipped playbook `formulacerta_lancar_formula` filled a real new quote (60 CAP, two components, stock notice answered by OCR rule) and stopped before OK; the quote was cancelled.
- **For the pharmacists:** `docs/integrations/formulacerta-avisos.md` lists every FórmulaCerta notice seen, with the proposed robot behaviour, for their decision.
- **Branch:** `feature/milestone-2-quotes`.
- **Next:** plan 20, saving and the after-OK dialog sequence, then reading the quote back.

## Status — 2026-10-18 (plan 16: whole prescription file + one extraction across MobyPharma and Orçamentos; end-to-end event still to be seen on the operator's PC)

## Status — 2026-10-12 (first real pharmacy system test: FórmulaCerta works end to end after two fixes and plan 14; see "First test on a real pharmacy system")

## Status — 2026-10-07 (plan 10 done; see below)

## Status — 2026-10-04

**Milestone 1 is functionally complete and verified by automated tests on Windows; a person-driven visual walkthrough on a Windows 10/11 desktop is the only open acceptance item.**

- Active plan: `docs/superpowers/plans/2026-09-27-trigger-runtime-events-and-test-mode.md` (plan 3 of 3). Tasks 1–5 implemented. Task 5 Step 3 (manual acceptance by a person) is partially open — see below.
- Branch: `claude/implementacao-progresso-testes-dgszzj`.
- Test evidence: GitHub Actions runs 37168362725 (push) and 37168362358/37168363903/37168365381/37168366872/37168368026 (repeat dispatches) on commit `f347080` (`windows-latest` = Windows Server 2025, x64), Release, warnings as errors: **210 tests, 210 passed, 6 of 6 runs green** — Domain 58, Infrastructure 29, Application 73, Windows 15, Windows integration 35 (real TestTarget/Desktop processes, real UI Automation, real cursor, real SQLite + DPAPI). This also executes, for the first time, the tests earlier sessions could only approve by reading (`MultipleInstanceTests`, the PID-scoped `SelectorResolutionTests`, `OverlayExclusionTests`) — all pass.

### What works (and the test proving it)

| Milestone criterion (spec §14) | Evidence |
| --- | --- |
| 1. Start the Agent and detect the TestTarget | `DesktopWalkthroughTests`, `ProcessIdentityDiscoveryTests`, `MultipleInstanceTests` |
| 2. Select fields and buttons visually | `DesktopWalkthroughTests` (real OS cursor over TestTarget, Agent shows and confirms the element), `ElementInspectionTests` |
| 3. Assign meanings, stages and actions | `DesktopWalkthroughTests` (capture + emit for Add, finish for Finish), `TriggerActionsBuilderTests` |
| 4. Save and reload | `DesktopWalkthroughTests`, `MilestoneFlowTests`, `JsonConfigurationStoreTests` |
| 5. Re-find elements with diagnostics | `SelectorResolutionTests` (incl. moved layout, ambiguity), test mode in `DesktopWalkthroughTests` |
| 6–8. Detect Add, capture fields, persist `item_added`; detect Finish, emit `budget_finished` | `MilestoneFlowTests`, `DesktopWalkthroughTests`, `SessionCoordinatorTests` |
| 9. Show values, events, confidence and failures in test mode | `TestModeViewModelTests` (value, provider, confidence, Portuguese failures), `RuntimeMonitorViewModelTests` (monitoring, events with values, rejected-field errors), `DesktopWalkthroughTests` |
| Only tested configurations activate; edits invalidate approval | `MilestoneFlowTests`, `AgentRuntimeTests`, `DesktopWalkthroughTests` |
| Encrypted at rest, no values in logs, ordered after restart | `MilestoneFlowTests`, `DesktopWalkthroughTests`, `SqliteEventOutboxTests` |

### This session's changes (Task 5)

- **CI**: `.github/workflows/ci.yml` builds and runs every suite on `windows-latest` (the development machine's Smart App Control blocks had left several suites unexecuted).
- **Fix — test hang**: `TestModeViewModel` deadlocked under xUnit (defaulted to the constructing thread's dispatcher and `Invoke`d from a pool thread). Without an explicit dispatcher it now updates inline.
- **Fix — process identity**: `Prescriva.Agent.TestTarget.exe` (the configurator's suggested value) never matched a running process; `ProcessIdentity` normalizes `.exe` for discovery and window location.
- **Fix — button selection**: pointing at a button selected its anonymous caption `Text` element (found by the cursor-driven walkthrough). The inspector promotes an anonymous caption to its interactive container.
- **Fix — stale monitor list**: two diagnostics raised in the same millisecond could be displayed out of order, leaving only one trigger shown as monitored (walkthrough failed 3 of 5 CI runs; the attached technical log proved both watches were live). The monitor now reads its lists under the lock and ignores out-of-date outbox reads; 6 of 6 runs green afterwards. The concurrency unit test added for it passes on old and new code alike (the race needs WPF dispatcher reordering), so the repeated walkthrough is the evidence.
- **Runtime**: `TriggerWatchStarted` (visible monitoring state) and `TriggerWatchFailed` (typed error, once per streak, with automatic retry every 2 s) diagnostics; `ITriggerProvider.WatchEstablished`. `UiAutomationRuntimeFactories` composes PID-scoped UIA providers.
- **Desktop**: trigger actions (capture, emit, clear, transition, finish/cancel) via `TriggerActionsBuilder`; configuration summary; test mode hosted (`Prepare Test`), shows values read; Activate/Stop through `RuntimeMonitorViewModel` with Portuguese diagnostics and persisted events; data in `%LOCALAPPDATA%\Prescriva\Agent` (`PRESCRIVA_AGENT_DATA` override); retention applied at start-up.
- **Docs**: `docs/architecture/*`, `docs/roadmap.md`, `docs/testing/milestone-1-manual.md`, README, setup, testing.

### Second round (same day): closing the remaining acceptance gaps

- **Spec §10 gaps closed**: visible pending-events alert (`OutboxCapacityPolicy` in the monitor, thresholds 1000/5000, `PRESCRIVA_AGENT_OUTBOX_WARNING`/`_CRITICAL`) and explicit, confirmed *Clear Local Data* (all events + `VACUUM`, technical log emptied, configurations kept; refused while monitoring).
- **Fix**: closing the monitored application no longer shows a red `TriggerWatchFailed` — a watch failure is reported only if it outlives the 2 s retry delay (RED observed on CI run 37215833760, GREEN 37215836025).
- **Fix**: preparing a new test resets the test-mode status, so a previous verdict is never shown for the new run.
- **Automation of the remaining manual rows**: `DesktopResilienceWalkthroughTests` (restart + reload, approval re-earned, empty required field rejected, capacity alert, app closed mid-session, cleanup, moved layout, ambiguity); the hover step now asserts the overlay window is within 2 px of the control and saves screenshots (reviewed: outline around the text box and the Add button).
- **x86**: CI publishes a self-contained `win-x86` TestTarget and re-runs 30 integration tests against it (bitness asserted) — all pass.
- Evidence: run 37216735104 — Domain 58, Infrastructure 31, Application 76, Windows 15, integration 37 (217 total) + 30 on x86, all passing.

### Plan 4 — Configuration lifecycle (2026-10-04)

Plan: `docs/superpowers/plans/2026-10-04-configuration-lifecycle.md`. All 5 tasks implemented, each with its test first (RED observed on CI for Tasks 2, 3 and 4: runs on f22b877, e36812a, 6076fa6).

- `IApprovalStore` / `JsonApprovalStore` (approvals persisted next to configurations; corrupt/foreign files never approve) and `ApprovalService` (`NotTested` / `Approved` / `ChangedSinceTest` for the current content hash).
- `IntegrationEditorViewModel`: `RemoveField` (refused while captured, forgets the resolved handle — closes the plan-2 stale-handle limitation), `UpdateField`, `RemoveTrigger`, `ReplaceTriggerActions`, `RemoveStage` (refused while used), duplicate IDs refused on add.
- Desktop: approval state line, Approve records through the service, Activate uses the stored approval for the current content, edit buttons with refusals shown.
- Evidence: run 37221107495 (push) and 37221111403 (PR) on 18a463c — Domain 58, Infrastructure 43, Application 90, Windows 15, integration 38 (244) + 30 x86, all passing; `DesktopConfigurationLifecycleTests` passed first time.
- Observed once (PR run 37220266848): the x86 pass hung 5 min in `ElementInspectionTests` (TimedOut or Cancelled test; TestTarget alive). Not reproduced in 6 following runs. **Root-caused 2026-10-05 (plan 8 session)**: the repeat on run 37387532470 named `FromPointAsync_returns_TimedOut_when_the_timeout_is_effectively_immediate`; a race in `AutomationDispatcher.RunAsync` (deadline flipped to cancelled just before the item ran; the item returned without completing the task and disposed the registration whose callback would have) left the caller awaiting forever. Reproduced locally (3 hangs in 20,000 calls), fixed (0 in 200,000), regression test in `AutomationDispatcherTests`.

### Plan 5 — Selector resilience (2026-10-04)

Plan: `docs/superpowers/plans/2026-10-04-selector-resilience.md`. Tasks 1–4 implemented, each test first (RED: Domain locally; structural integration tests on CI run 37225379923; health on fdfde7f).

- Weights v2 (version 2) and confidence relative to the selector's own signals — fixes a spurious `SelectorFallback` on every capture (a full AutomationId+ControlType match was 0.65).
- `StructuralSignals`: label (LabeledBy / preceding sibling Text), ancestors, relative bounds — recorded by the inspector (plus FrameworkId) and computed for resolution candidates. Controls without AutomationId are now found by their label, also after moving; duplicate labels are ambiguous.
- TestTarget: labelled, unnamed fields "Observações:" / "Lote:" (swapped by `alternate`), `duplicate-labels` variant.
- `IntegrationHealthTracker` + monitor line: Healthy / Degraded / Broken with reasons.
- The Agent's hover/confirmed text shows the detected label.
- Evidence: run 37226388696 (push) / 37226391838 (PR) on 955f124 — Domain 66, Infrastructure 43, Application 97, Windows 15, integration 42 (263) + 30 x86, all passing.

### Plan 6 — File and image fields (2026-10-05)

Plan: `docs/superpowers/plans/2026-10-05-file-and-image-fields.md`, from the user's decisions (path box, displayed image, drag-and-drop; keep a copy of the original file; 10 MB). All 5 tasks implemented, tests first (RED: field kind locally; capture on CI run of 8804d06; runtime/test mode/GC on 34fef91).

- `FieldDefinition.Kind` (`Text`/`File`); text-only configurations keep their content hash (characterization test), so existing approvals stay valid.
- `IAttachmentStore` / `SqliteAttachmentStore`: encrypted content and file name, SHA-256 de-duplication, 10 MB limit, garbage collection, cleanup.
- `FileFieldCapture`: path → exact copy; image/drop zone → PNG of the control's rectangle, refused if covered.
- Runtime stores the attachment before the event; test mode and monitor show files by name/size/origin; Desktop "Field type" selector.
- TestTarget: "Receita (arquivo):" path box with "Procurar...", image drop zone with a deterministic sample.
- Evidence: run 37347134594 on 58e64da — Domain 66, Infrastructure 55, Application 105, Windows 15, integration 48 (289) + 30 x86, all passing; `DesktopFileFieldWalkthroughTests` passed first time.
- Limit (accepted): a dropped image's original file is not exposed by UI Automation; the on-screen image is captured instead and marked `screen`. Real Explorer drag-and-drop is not automated (the sample button stands in for it).

### Plan 7 — Spec gap closure and start with Windows (2026-10-05)

Plan: `docs/superpowers/plans/2026-10-05-spec-gap-closure-and-startup.md`, after the user asked whether the Agent starts with the PC (it did not) and whether OCR works (it did not exist; it is the next plan). Tasks 1–5 implemented, tests first (RED observed: domain/engine and coordinator locally; outbox migration and Desktop on CI).

- **Content revision in events** (spec §9): `DomainEvent.ConfigurationRevision` = the configuration's content hash; outbox schema v2 (`configuration_revision`), v1 queues migrated in place.
- **Bounded retry** (spec §11): `CaptureRetryPolicy` — 3 attempts (100/200 ms), cancellable, full re-resolution each time; never for ambiguity, unsupported controls, too-large or missing files.
- **Test-mode evidence** (spec §8): matched signals with weights, lead over the runner-up (`SelectorMatch.RunnerUpScore`), duration per field and trigger, `SelectorFragility` warnings (no AutomationId, confidence < 80%, narrow lead, position-only), trigger effects.
- **Fix (found by CI)**: stopping monitoring could surface `OperationCanceledException` from `ActivateAsync` when the cancellation landed outside the instance poll's wait (MilestoneFlowTests on run 37356461035); cancellation is now always a normal end.
- **Start with Windows**: opt-in *Iniciar com o Windows* (per-user Run key, `--background`); tray icon (show / stop / exit, monitoring tooltip, notifications); closing while monitoring hides to the tray; one Agent per data directory (a second start shows the running window); `--background` resumes only the integration left active (`monitoring.json`, written on Activate, cleared by Stop) and only while it is approved for its content.
- Evidence: run 37358301438 on 77da6cb — Domain 68, Infrastructure 62, Application 130, Windows 18, integration 51 (329) + 30 x86, all passing; `DesktopStartWithWindowsTests` (3) passed first time.
- Not automated: a real Windows sign-in, the tray icon/menu/notification rendering (manual rows 24–25).

### Plan 8 — OCR text fields, and closing the remaining pending items (2026-10-05)

Plan: `docs/superpowers/plans/2026-10-05-ocr-text-fields.md`, from the user's request for OCR; then the user asked to finish everything pending.

- **OCR fields** (`FieldKind.OcrText`): the configured control's on-screen image (refused when covered) is read with the offline Windows OCR (`Windows.Media.Ocr`); Portuguese preferred (pt-BR > pt > profile > first installed); small images enlarged; image discarded; typed `OcrUnavailable` (not retried) with test-mode guidance to install the Portuguese OCR language. TestTarget: "Receita digitalizada" image with deterministic samples. RED observed on CI run 37387532470 (3 `OcrCaptureTests` against a stub); GREEN on run 37387804617 (`OcrCaptureTests` 3/3, `DesktopOcrFieldWalkthroughTests`). The CI runner has only the en-US recognizer (probe step in `ci.yml`), so Portuguese recognition of accented text is a manual row (26).
- Windows-facing projects target `net10.0-windows10.0.19041.0` (ADR-001 update).
- **x86 hang root-caused and fixed** (dispatcher race, above).
- **Dispatcher recovery**: a call running past 30 s (a hung target application) is abandoned on the next call; queued work moves to a fresh STA thread (`RecoveredCount`). Validated locally with a non-STA copy; `AutomationDispatcherTests` on CI.
- **Selector weights version** recorded on approvals; an approval tested under other weights no longer activates (existing approval files load as the current version).
- **IDs**: semantic IDs unique ignoring case; loading a missing configuration fails with `CONFIGURATION_NOT_FOUND` (no file path); saving a case-variant of an existing configuration ID fails with `CONFIGURATION_ID_CONFLICT` (on Windows it would overwrite the other file).
- **Fix found by CI**: the new TestTarget column made the last row taller, pushing the image drop zone off a 1024×768 screen at the default window position (`FileCaptureTests`); compacted, and the test now places the window.

### Plan 9 — Microsoft Store packaging (2026-10-06)

Plan: `docs/superpowers/plans/2026-10-06-store-packaging.md`. User decisions: distribute through the Microsoft Store (it signs, hosts and updates; no certificate to buy — Azure Artifact Signing does not serve Brazil), publisher an individual.

- `IStartupRegistration` is asynchronous with real states (`DisabledByUser` in Task Manager, policy); `PackagedStartupRegistration` (package `StartupTask`) when the process has package identity, the Run key otherwise; `StartupLaunch` starts in the background on `--background` or a startup-task activation.
- `packaging/`: MSIX manifest template (full trust, startup task off by default), logos, `build-msix.ps1` (self-contained win-x64 publish → makeappx; optional test signing).
- CI builds the MSIX, signs it with a throwaway test certificate, installs it and runs `PackagedAgentTests` (starts from `WindowsApps` with package identity; the option turns the package's startup task on/off as recorded by Windows; the reopened Agent shows it). Artifact `msix-package` (+ test certificate).
- `docs/privacy-policy.md` (pt-BR/en) and `docs/release/microsoft-store.md` (Partner Center steps, identity, capability justification, hidden listing, listing text).
- Evidence: run 37395451712 on 10b4a7a — Domain 74, Infrastructure 68, Application 136, Windows 26, integration 55 (359) + packaged 1 + x86 30, all passing.
- **Waiting on the publisher**: Partner Center account, reserved name and its three identity values, a public privacy-policy URL with a contact e-mail; then build with that identity and submit. Real sign-in start from the Store package: manual row 27.

### Plan 13 — Selecting a small field inside a larger one (2026-10-09)

Plan: `docs/superpowers/plans/2026-10-09-innermost-selection.md`.

- While marking, **Shift** marks the smallest element under the pointer (`InspectionDepth.Innermost`).
- **Ctrl**, pressed while the other program is in front, confirms the outlined element.
- Only the Shift/Ctrl up/down state is read, and only while marking.
- TestTarget gained a product row `ProductCard` whose price text has no AutomationId: pointing at the price selects the row; Shift selects the price. The case of a program whose hit-test stops at an outer element whose children *are* exposed is handled by the same search, but no TestTarget fixture reproduces it (WPF's UI Automation hit-test ignores `IsHitTestVisible`); it is in manual row 35.
- RED observed locally for `InspectionControllerTests`. The Windows and E2E tests were pushed with the implementation.

- Evidence: run 37932186976 on 6a0ad74, all green on attempt 2.
- **Observed once, not root-caused:** on attempt 1, `DesktopResilienceWalkthroughTests` stopped at *Aprovar configuração* (disabled after the first test run). That test does not use the changed code, and it passed on the previous commit and on the re-run. If it recurs, capture the test-mode field results to see which field failed.

### Plan 12 — Screen-image fields (2026-10-09)

Plan: `docs/superpowers/plans/2026-10-09-screen-image-fields.md`.

- New field type *Imagem exibida na tela*: `FieldKind.ScreenImage`.
  - Always captures the control's on-screen image as PNG, even when the control exposes text.
  - Refused when anything covers the control.
  - Stored as an attachment, like file fields.
- RED observed locally:
  - `SessionCoordinatorTests` and `IntegrationTestRunnerTests` (behaviour);
  - `FieldKindTests` (compilation).
- The new `FileCaptureTests` cases were pushed together with the fix, so their RED was not observed on CI. They would fail without it: the field would be read as text, with no image.

### Plan 11 — CSV export (2026-10-08)

Plan: `docs/superpowers/plans/2026-10-08-csv-export.md`.

- `EventCsvExporter` (Application) writes CSV for Excel pt-BR:
  - `;` separator, CRLF, RFC 4180 quoting;
  - one row per event, fixed columns plus one per field;
  - formula-injection guard that keeps plain numbers;
  - file fields shown by file name.
- `IEventOutbox.ReadExportableAsync`: pending and delivered events, never quarantined ones.
- `RuntimeMonitorViewModel.ExportCsvAsync` writes UTF-8 with BOM.
- Step 5: *Exportar eventos (CSV)...*, using a save dialog. The status warns that the file is not encrypted.
- Each step was test-first (RED observed: exporter and outbox locally; view model by compile).
- `DesktopWalkthroughTests` exports through the real save dialog and checks the rows.
- Evidence: run 37800907393 on 51d2f03 — Domain 74, Infrastructure 70, Application 164, Windows 26, Windows integration 66 (1 skipped: the Store Calculator), packaged 1, x86 31, Store screenshots 1 — all passing.

### Plan 10 — Guided configurator and real Windows apps (2026-10-06/07)

Plan: `docs/superpowers/plans/2026-10-06-guided-ui-and-real-apps.md`. All 4 tasks implemented.

- **Open-program list**: `OpenWindowInfo` / `IOpenWindowSource` / `OpenWindowFilter` (Application), `Win32OpenWindowSource` (Windows). Lists visible, titled, unowned, non-cloaked top-level windows, never the Agent's own. Kept in memory only, never logged or saved; only app name, title, process, PID and icon.
- **Probe of the CI runner** (`scripts/probe-real-apps.ps1`, every build): `windows-latest` (Server 2025) has the **classic** calculator `win32calc` (numeric AutomationIds) and the classic Notepad (text area `15`). The Store Calculator (`CalculatorApp` in `ApplicationFrameHost`) is **absent**, so the UWP hypothesis could not be reproduced on CI.
- **Bug found by the real-app tests (RED run 37544709491, GREEN afterwards)**: a plain label with no pattern (the Calculator display, a TestTarget label) was `UnsupportedPattern`. Capture now reads a `ControlType.Text` element's Name; a button's Name stays unsupported.
- **ApplicationFrameHost**: the window locator, instance discovery and test preparation identify a framed Store app by its content process. An empty frame matches nothing. Preparing a test with several identical windows open is refused rather than picking one. Covered by `RealCalculatorTests.The_Store_calculator_...`, which **skips on CI** (no Store Calculator) — **needs human verification on Windows 10/11** (manual row 30).
- **Guided configurator** (`MainWindow`): five steps (1 Escolher programa, 2 Marcar campos, 3 Marcar botões, 4 Testar, 5 Ativar).
  - Program list with search, refresh and icons; manual entry under *Opções avançadas*; list of saved integrations (`JsonConfigurationStore.ListIds`).
  - Field and button lists with edit/remove; capture-field checkboxes; IDs generated from friendly names (`SemanticIdGenerator`); the only stage is created with the first field.
  - Explained empty states and actionable errors; light theme. All earlier behaviour is kept.
  - The E2E driver reaches controls on other steps by opening them; assertions are unchanged. The main walkthrough now chooses TestTarget from the list.
- **Bug found by screenshot review**: the selected step's white header text was inherited by the step content, so test-mode results were drawn white on white. Fixed in 784452e.
- **Real apps**:
  - `RealCalculatorTests` (listed by content process, display read after 3 9 2, `=` detected, instance discovered, Store variant skipped with reason);
  - `RealNotepadTests`;
  - `DesktopCalculatorWalkthroughTests` — the whole guided flow against the real Calculator, passing on CI.
- Screenshots of the five steps (CI run 37549925964): `docs/screenshots/guided-ui/`.
- Evidence: run 37549925964 on 5661329 (same counts as run 37548039030 on f016abb) — Domain 74, Infrastructure 69, Application 150, Windows 26, Windows integration 66 (1 skipped: the Store Calculator), packaged 1, x86 31, Store screenshots 1 — all passing.

### First test on a real pharmacy system — FórmulaCerta 6.0 (2026-10-12)

Branch `fix/formulacerta-inspection` (from `master` 41995f4), on the operator's PC, against the *Saídas > Receitas* test screen (`FCReceitas.exe`, Delphi VCL). Details and per-step results: `docs/testing/milestone-1-manual.md`, section "Real pharmacy system". Nothing was saved or issued in the pharmacy system; the diagnostic report stays on that PC.

- **Fix — slow programs never showed an element while marking**: one inspection takes 250–330 ms on this program and every 80 ms poll cancelled the one in flight. `InspectionController` now lets a repeated observation of the same point and depth join the inspection in flight. RED `Repeated_observations_of_the_same_point_wait_for_the_slow_inspection_in_flight` (locally), GREEN after.
- **Fix — Delphi forms missing from the program list**: every Delphi form is owned by the zero-size `TApplication` window, and the list skipped owned windows (or offered the zero-size window, which has no fields). `Win32OpenWindowSource` now lists windows owned by a zero-size owner and never lists zero-size windows. RED `A_form_owned_by_a_zero_size_application_window_is_listed_instead_of_its_owner` (both owner states, locally), GREEN after.
- Evidence (local, operator's PC): Release build 0 warnings; Domain 74, Application 171, Infrastructure 71, Windows 26; `OpenWindowSourceTests` 5/5. The rest of the integration suite was not run there (it drives the real cursor over the pharmacy system) - CI must run it.
- **Plan 14 — Win32/Delphi selectors** (`docs/superpowers/plans/2026-10-12-win32-delphi-selectors.md`), same day, each test first:
  - `ElementFingerprint.WindowPath`; the matcher scores it in the AutomationId's slot and weight (weights stay v2, approvals stay valid). RED: `SelectorMatcherTests.WindowPath_*` (locally).
  - Content hash folds `windowPath` only when present (RED `WindowPathSelectorTests`; `FieldKindTests` characterization unchanged).
  - Inspector: an AutomationId equal to the element's window handle is dropped and the window path recorded; ancestors/labels dropped; a name kept only for captioned controls (a Delphi date box is named by its value). Resolver: native child-window enumeration for window-path selectors, `FromHandle` for the winner only. Trigger: `Win32ClickWatcher` (mouse capture start/end on the button's window) for window-path buttons.
  - TestTarget `--layout-variant win32-form`: a native window with VCL-style handle IDs, own window classes, a capturing vendor-style button and a value-named date box. `Win32WindowPathTests` (3): field found and read after a restart; click on a button without Invoke detected; value-named control marked without its name. RED observed locally for each; `SelectorFragilityTests` (window path is not "position only") too.
  - The `InspectionControllerTests` window-path test only had a compile-time RED (property added with it).
  - Evidence (local): build 0 warnings; Domain 76, Application 173, Infrastructure 73, Windows 26; integration Automation/Processes/Overlay 52, Runtime/RealApps 9 (+1 skipped). EndToEnd and x86 not run locally - CI must run them.
  - **FórmulaCerta second run: steps 1–5 pass**, also after the Receitas screen was closed and reopened (new process, new handles). See the manual.
- **Plan 15 — Templates ("Modelos") with a guided checklist** (`docs/superpowers/plans/2026-10-12-integration-templates.md`), same day:
  - `IntegrationTemplate`/`TemplateItem`/`TemplateValidator`/`ITemplateStore` (Application), `JsonTemplateStore` (Infrastructure: shipped `Templates\*.json` next to the executable + own templates in `%LOCALAPPDATA%\Prescriva\Agent\templates`; an own template replaces a shipped one with the same ID; broken files left out), `TemplateGuideViewModel` (Desktop: start, suggestion, mark, test field/button, skip, adjust, add, remove, save as own template; buttons read the marked fields). UI: step 1 *Começar por um modelo*, step 2 *Roteiro* card (`MainWindow.Templates.cs`). Editor: `SetFieldSelector`, `SetFieldKind`, `SetTriggerSelector`.
  - Shipped templates: FórmulaCerta *Orçamento*, *Receita*, *Foto da receita (MobyPharma)*, with suggested window-path positions taken from the real screens.
  - OCR: a grid's cells come back as separate lines; `OcrText.Rows` rebuilds rows from line positions ("cell | cell"). RED `OcrTextTests.Cells_side_by_side_become_one_row_in_reading_order`.
  - Tests: `IntegrationTemplateTests` (7), `JsonTemplateStoreTests` (5, incl. every shipped template valid), `TemplateGuideViewModelTests` (14) - their RED was compile-time only (new types). Evidence (local): build 0 warnings; Domain 76, Application 194, Infrastructure 78, Windows 28; integration Automation/Processes/Overlay/Runtime/RealApps 61 (+1 skipped). No EndToEnd test drives the new UI yet - **CI/EndToEnd coverage of the checklist UI is still to write**; it was exercised by hand on FórmulaCerta (manual row 36).
- **Plan 16 — Whole prescription file and one extraction across two programs** (`docs/superpowers/plans/2026-10-18-attachment-file-and-cross-program.md`, 2026-10-18, branch `feature/attachment-file-cross-program`):
  - MobyPharma writes every opened attachment to `%TEMP%[\<session>]\anexo_YYYYMMDD_HHMMSSmmm.<ext>`; `FieldKind.RecentFile` + `FileSource` copy the newest one whole while the viewer is open (`RecentFileFinder`, `UiAutomationCaptureProvider`), so the attendant's zoom/scroll does not matter and the screen is never touched.
  - `ProcessRouting` (Application): fields/buttons of another program are resolved/watched there (`ApplicationInstance.AnyProcess`, mapped to an unscoped provider in `UiAutomationRuntimeFactories`); used by `AgentRuntime`, test mode and the checklist's click test.
  - Templates: per-item `Program`, `FileSource`, `Role` (send / keep / discard). New shipped template *Receita completa (anexo + orçamento)*: main program MobyPharma's order window; attachment kept on *Nova Fórmula*/*Novo Produto*; one `receita` event on the Orçamentos *OK*; *Cancelar* discards.
  - Tests first: `ConfigurationValidatorTests` (file source), `RecentFileFieldTests`, `RecentFileFinderTests`, `RecentFileCaptureTests` (RED observed), `ProcessRoutingTests`, `AgentRuntimeTests.A_button_of_another_program_...` (RED observed), `TemplateGuideViewModelTests` (4 new). Several REDs were compile-time only (new types).
  - Evidence (local): build 0 warnings; Domain 77, Application 204, Infrastructure 80, Windows 31; integration (non-clicking folders) 63 (+1 skipped). FórmulaCerta: the file item copied the whole original PDF (43 KB); simulated as an attendant with stand-in buttons (manual, "One extraction across two programs"), test mode passed and activation produced one `receita` event with the whole file and the fields, and none after a discard. Still to see with the real buttons: *Nova Fórmula*/*Novo Produto* (disabled on the order used) and *OK* (saves - only on a test record, clicked by a person). Window-path indices of the viewer's bottom navigation buttons vary between orders (not used by the template).
  - Components (same day): OCR of the real components grid is unreliable on data rows (digits, letters and decimal comma wrong; unit/value missing). The three FórmulaCerta templates now list `componentes_imagem` first as the source and `componentes_ocr` (renamed from `componentes`) as a cross-check only; the receiving side's rules are in `docs/integrations/formulacerta-receita.md`. Template data only - no code change. Possible later plan: exact components from a read-only lookup of the quote's items.
  - Quantities and several formulas (same day): new template fields `quantidade` + `quantidade_unidade` (Volume, e.g. 90,00 CAP), `qtd_prescrita`, `qtd_potes`, `numero_orcamento`, `serie_formula` (positions mapped on a quote in consult mode; values read there by window text: 90,00 / CAP / 90 / 1 / quote number / 1). New button role `SendAndKeep` (capture + emit, no clear; RED observed in `TemplateGuideViewModelTests.A_send_and_keep_button_...`): *Receita completa*'s *OK* uses it, so each formula of a quote (FórmulaCerta asks "more formulas?" after *OK*) sends its own `receita` event with the kept prescription file. Not yet seen: the real *OK* click on a test quote (does the dialog cover the grid before the image is taken? is the quote number already set in a new quote?). One Application test failed once and passed on three reruns - flaky, not identified.
  - **Test quote 899119 (same day, simulated data): the Orçamentos *OK* is not detectable as a click** (it never captures the mouse; it opens a modal question at once). The right moment is the "Mais Fórmula(s)?" `TMessageForm` (title `Orçamento <n>`): formula saved, quote number set, every field and the grid image read correctly while it is shown. Its title is shared by the final "inclusão do orçamento?" question, so the trigger must tell them apart (or events must be deduplicated by `numero_orcamento` + `serie_formula`). **Done in plan 17** (`docs/superpowers/plans/2026-10-18-window-shown-trigger.md`): trigger event `WindowShown` (`Win32WindowShownWatcher`: top-level window of the program, class = selector `ClassName`, title starts with selector `Name`) and `EmitEventAction.UniqueBy` (no event while those fields keep the values of the last event of that type; cleared with the session). The *Orçamento* and *Receita completa* templates now send on "Mais Fórmula(s)?" (`TMessageForm`, `Orçamento `), role send-and-keep, unique by `numero_orcamento` + `serie_formula`. Walkthrough on test quote 899120: 3 questions → 2 events (one per formula), none for the inclusion question. Then, after a code review (fixes listed in the plan's Result): seen through the Agent's own window - checklist, test mode (question detected after 69.5 s; the wait for a question is now 3 minutes), approval, activation: one `orcamento_lido` per formula, "não repetido" shown for the inclusion question. *Cancelar* of Orçamentos captures the mouse (detected). Open: another "Orçamento …" message before the save would be taken as the question (check with the operator); the *Receita completa* template end to end with *Nova Fórmula* (button disabled on the orders seen so far).
- **Plan 18 — Plug-and-play setup** (`docs/superpowers/plans/2026-10-18-plug-and-play-setup.md`): window rules match the exact title or part of it (`WindowRule`, version-proof: "Orçamentos"); template items of another program are aimed by their title part (no need to be open); `IntegrationTestRunner.VerifyAsync` (fields read, triggers watched = `Ready`) approves; step 1 **Configurar automaticamente** marks every suggestion, saves, waits for the screens with instructions, verifies, approves, activates and turns on start with Windows. Walkthrough on FórmulaCerta: one click → ready; events per formula, no repeat; reopen and Agent restart (`--background`) resume by themselves. *Receita completa* also tested end to end (order 360281 taken by PAULO; quote 899124 with two formulas → two `receita` events, each with the whole prescription PDF). Left on the operator's PC: integration `formulacerta_receita_completa` active and the Run key pointing at the repo's Release build. Note for templates: a JSON property written twice is now refused (one shipped template had `fieldKind` twice).
- **Still open for FórmulaCerta**:
  1. Activating a vendor button with the keyboard is not detected (mouse only).
  2. Reading a field filled with real prescription data was not checked (no record opened); the date filter read `14/07/26`.
  3. ~~Resolution TimedOut~~, ~~volatile AutomationId~~, ~~no labels~~: solved by the window path (plan 14).
  5. After the outline grows over a large container, hit-testing from inside the Agent returns the Agent's own outline window, so inner fields cannot be reached until marking restarts (`OverlayExclusionTests` accepts that outcome; a RED test can put the overlay over the whole TestTarget window and point at a field inside).
  6. Window rule includes the program version ("FórmulaCerta - Receitas 6.0.1164").
  7. Test-mode failure text is English ("Could not resolve the element for trigger ...").

### Not verified / known issues

- **Person on a Windows 10/11 desktop**: still not performed. Every row of `docs/testing/milestone-1-manual.md` is now automated except rendering on a real display at non-100% DPI scaling; CI is Windows Server 2025 at 100%. A short look-over by a person is still the last sign-off step.
- Fields and buttons are chosen from lists now; a field's selector can still only be replaced by removing and re-adding it.
- Store (UWP) apps framed by `ApplicationFrameHost` (Windows 10/11 Calculator): implemented but only unit/skip-covered on CI — manual row 30. Windows 11's new Notepad, Electron, Java and canvas apps: see the limitations in the manual walkthrough.
- Display scaling 125–150% for the new step-by-step window: manual row 32.
- Older limitations below remain unless marked resolved.

### Important files

`src/Prescriva.Agent.Desktop/MainWindow.xaml(.cs)` (composition root), `src/Prescriva.Agent.Desktop/Monitoring/RuntimeMonitorViewModel.cs`, `src/Prescriva.Agent.Desktop/Configuration/TriggerActionsBuilder.cs`, `src/Prescriva.Agent.Application/Runtime/SessionCoordinator.cs`, `src/Prescriva.Agent.Windows/Runtime/UiAutomationRuntimeFactories.cs`, `src/Prescriva.Agent.Windows/Processes/ProcessIdentity.cs`, `tests/Prescriva.Agent.Windows.IntegrationTests/EndToEnd/*`.

### Exact next action

0. Push `fix/formulacerta-inspection` and let CI run the whole suite (incl. EndToEnd and x86); add an EndToEnd test of the template checklist (TestTarget `win32-form` + a test template); review and merge. Then, with the operator, run the *Orçamento* template on a quote with a prescriber and components filled in: confirm the prescriber fields' meaning and whether the values are still on screen when *OK* is clicked (otherwise choose *Recálculo*).
1. A person runs `docs/testing/milestone-1-manual.md` on a Windows 10 or 11 x64 desktop (ideally once at 125–150% display scaling) and records the result in its table; fix anything it finds (with a test first).
2. Publisher: create the Partner Center account and reserve the name; send the three identity values (see `docs/release/microsoft-store.md`); then build with them and submit.
3. A person verifies rows 29–32 on Windows 10/11 (Store Calculator, new Notepad, scaling).
4. Next plans (`docs/roadmap.md`): event transport to the backend (needs the backend's API).

---

# History

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

Plan: `docs/superpowers/plans/2026-09-27-windows-inspector-and-capture.md`. All 5 tasks are complete (each task-reviewed, each with a fix round applied and re-reviewed clean), and the plan's final whole-branch review has also been performed and its fix wave applied (see "Final-review fix wave" below).

- **Task 1**: `Prescriva.Agent.TestTarget` — a real, deterministic `net10.0-windows` WPF exe with all 12 required `AutomationProperties.AutomationId`s (`MedicationTextBox`, `ConcentrationTextBox`, `QuantityTextBox`, `FormComboBox`, `ItemsGrid`, `NextButton`, `BackButton`, `AddButton`, `FinishButton`, `CancelButton`, `DynamicField`, `ToggleDynamicFieldButton`), a working `--layout-variant` argument (moves controls, keeps IDs stable), Add-appends-a-row and Finish-shows-completion-state. `Prescriva.Agent.Windows` and `Prescriva.Agent.Desktop` scaffolded as empty `net10.0-windows`/WPF projects (no logic yet). `TestTargetLauncher` (test helper) launches/waits/kills a real TestTarget process for integration tests.
- **Task 2**: `AutomationDispatcher` — one dedicated background STA thread owns all real UI Automation access; every operation accepts cancellation and a timeout; queued work started after cancellation/timeout never runs its body. `UiAutomationElementInspector` implements `IElementInspector` (`FromPointAsync`, `FindCandidatesAsync`) against real UIA. `ElementSnapshot`/`ScreenPoint`/`InspectionResult` and the failure vocabulary `ElementInspectionFailure`/`ElementInspectionFailureKind` (`Cancelled`/`TimedOut`/`WindowMissing`/`ElementUnavailable`) live in `Prescriva.Agent.Application.Inspection` — plain, UIA-free data, so Application-layer code can branch on failure kind without a compile-time reference to `Prescriva.Agent.Windows`. Both `IElementInspector` methods map a `Cancelled` failure to `OperationCanceledException` identically.
- **Task 3**: `UiAutomationSelectorResolver` implements `ISelectorResolver.ResolveAsync` by combining Task 2's real candidate enumeration with the foundation plan's pure `SelectorMatcher` scoring, producing a `SelectorResolution` (exact match / moved-layout match / `Ambiguous` / not found / window missing). `UiAutomationCaptureProvider` implements `ICaptureProvider.CaptureAsync`, trying `ValuePattern` → `TextPattern` → `SelectionPattern` in order and recording every attempt; a liveness probe (`element.Current.ControlType`) distinguishes a genuinely destroyed element from one that simply lacks a pattern (`TryGetCurrentPattern` silently reports "unsupported" for a dead provider instead of throwing). `ResolvedElementHandle` is a genuinely opaque token (its concrete `UiaResolvedElementHandle` and the live `AutomationElement` it wraps are both `internal` to `Prescriva.Agent.Windows`, closing even a reflection-based extraction attempt). `AutomationWindowLocator.FindDescendants` is the single shared helper both the inspector and the resolver use for window-descendant enumeration + `WindowMissing` mapping (no duplication).
- **Task 4**: `InspectionController` (Application layer, pure/testable with a fake `IElementInspector`) orchestrates a live pointer-driven inspection session: `StartAsync`/`ObservePointerAsync`/`StopAsync`/`ConfirmAsync`, throttles rapid pointer movement (a superseding `ObservePointerAsync` call cancels and atomically supersedes any still-in-flight one — the check-and-write happens under one lock, not as a separate check then a separate write, closing a real TOCTOU race an early version had), excludes elements belonging to the Agent's own process (`Environment.ProcessId` plus any caller-supplied extra IDs) so its own overlay never reports itself as a found element, and `ConfirmAsync` captures exactly what was already displayed without re-resolving. `ElementSnapshot` gained `ProcessId`/`ProcessName`/`WindowTitle` fields to support this. `HighlightOverlayWindow` (Desktop, WPF) is a real click-through, non-activating highlight window (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` set via P/Invoke after `SourceInitialized`); a real integration test (`OverlayExclusionTests`) proves the overlay is never reported as a found element by driving an actual message-pumped overlay HWND over a real TestTarget. `InspectorViewModel` is the thin WPF-bindable seam (no logic leaks either direction).

Verification commands (from the worktree root):

```powershell
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release
```

If `dotnet` isn't on PATH, locate the SDK executable (commonly `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` on Windows) and use its full path. As of Task 4's completion: Domain 58, Infrastructure 9, Windows.Tests 6, Application.Tests 10 (InspectionController), Windows.IntegrationTests 20 — all real tests (the integration tests launch and inspect a real `Prescriva.Agent.TestTarget.exe` process, and Task 4's overlay test drives a real message-pumped overlay window, on an interactive desktop; they are not mocked and will not run headlessly). `Prescriva.Agent.Windows.IntegrationTests` disables test parallelization at the assembly level (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) because its tests drive real, visible desktop windows at fixed screen coordinates that would otherwise race each other.

**Environment note — Windows Smart App Control:** on a machine with Smart App Control enforced (`HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy!VerifiedAndReputablePolicyState = 1`), `testhost.exe` can be blocked from loading a freshly-built, unsigned `Prescriva.Agent.Desktop.dll` (`FileLoadException`, CodeIntegrity event IDs 3033/3077/3118, "did not meet Enterprise signing level requirements"). Building `--configuration Release` fares better than Debug but is **not a guaranteed fix** — a Release build with no accumulated reputation can be blocked identically (observed on this machine during Task 4's fix round, after Release had passed reliably earlier the same day). `Unblock-File` does not help (this is reputation-based, not mark-of-the-web-based). No workaround was available without admin rights in this session. If this blocks `Prescriva.Agent.Windows.IntegrationTests`' Overlay tests (or, going forward, anything else that loads `Prescriva.Agent.Desktop.dll`) on a given machine, an administrator needs to adjust the Smart App Control policy or the build needs to be signed. See `docs/testing.md` for the same note.

- **Task 5**: `IntegrationEditorViewModel` (`src/Prescriva.Agent.Desktop/Configuration`) is the Application-facing orchestration layer for the vertical slice: create integration, select application, add stage/field/trigger with a validated semantic ID (`^[A-Za-z][A-Za-z0-9_]*$` — reject before the configuration is ever touched, so a rejected ID never partially applies), save (validates via `ConfigurationValidator` before ever calling `IConfigurationStore.SaveAsync`, so an invalid configuration is never persisted), reload (round-trips fingerprints through the store unmodified), and resolve + read a saved field's live value (caches the `ResolvedElementHandle` from a successful `ResolveFieldAsync` so a subsequent `ReadFieldValueAsync` can read through it; throws if called before a resolve). It never touches `AutomationElement` or any other Windows-only type, even though it lives in `Prescriva.Agent.Desktop`. `Prescriva.Agent.Application.csproj` grants `InternalsVisibleTo` to `Prescriva.Agent.Application.Tests` (alongside the existing grant to `Prescriva.Agent.Windows`) so its fakes can construct a test-only `ResolvedElementHandle` subclass for resolve/read-value tests — the type is still unconstructable from any assembly outside those two. 16 new tests (`IntegrationEditorViewModelTests`) cover the brief's four required scenarios (invalid semantic IDs rejected, unsaved edits visible via `HasUnsavedChanges`, save validates and refuses to persist an invalid configuration, reload preserves fingerprints) plus resolve/read-value happy and not-found paths. `Prescriva.Agent.Desktop` gained its first real entry point: `App.xaml` (`StartupUri="MainWindow.xaml"`, no code-behind, matching `Prescriva.Agent.TestTarget`'s pattern) and a genuinely runnable `MainWindow` (create integration/stage, start/stop live pointer inspection with the Task 4 `HighlightOverlayWindow` shown via a `DispatcherTimer` polling `GetCursorPos`, confirm a selection, assign a semantic ID and add it as a field, save/reload, resolve + read a saved field) — kept intentionally minimal, no MVVM framework, just event handlers calling into `IntegrationEditorViewModel`/`InspectorViewModel`. The csproj gained `OutputType=WinExe` and a `ProjectReference` to `Prescriva.Agent.Infrastructure` (for `JsonConfigurationStore`); `Prescriva.Agent.Application.Tests` and `Prescriva.Agent.Windows.IntegrationTests` both gained a `ProjectReference` to `Prescriva.Agent.Desktop` (net10.0 could not reference the net10.0-windows Desktop project — `Prescriva.Agent.Application.Tests`'s `TargetFramework` was changed to `net10.0-windows` to allow it). A new real, automated end-to-end test (`ConfiguratorVerticalSliceTests`, in `Prescriva.Agent.Windows.IntegrationTests`) drives the entire pipeline — real launched `Prescriva.Agent.TestTarget.exe`, real `UiAutomationElementInspector`/`InspectionController` pointer inspection, real `IntegrationEditorViewModel` wired to real `UiAutomationSelectorResolver`/`UiAutomationCaptureProvider`/`JsonConfigurationStore` (writing to a throwaway temp directory) — through create → inspect → confirm → assign semantic ID → save → reload (a second, independent view-model instance, simulating an app restart) → resolve → read, asserting the captured value matches a known value seeded onto the real control beforehand. It passed (`--configuration Debug`, this machine, 2026-09-29) — see `docs/testing/windows-inspector-manual.md` for the full account, including the honest, explicitly-scoped gap it leaves (it does not drive `Prescriva.Agent.Desktop.exe`'s own compiled WPF UI via simulated mouse/OS input — judged impractical within reasonable effort per the task brief's own escalation clause) and what a manual launch-and-observe check could and could not confirm about that UI. No x86 sample application exists anywhere in this repository, so the brief's x86 repeat-verification step was not performed and no x86 compatibility claim is made.

**The Windows Inspector and Capture plan (Tasks 1–5) is now implemented, and its final whole-branch review's fix wave has been applied.**

### Final-review fix wave

The plan's final whole-branch review, run against the actual runnable `Prescriva.Agent.Desktop.exe` (not just its unit-testable layers), found three real defects that no per-task review had caught because each task's own review scope stopped at that task's boundary. All three are now fixed, on top of `ca2ff05`:

1. **Desktop.exe could not save any configuration.** `ConfigurationValidator` rejects any configuration with zero triggers (`MISSING_TRIGGERS`), but `MainWindow` had no UI to add one, so clicking Save always failed. Added a minimal "4b. Add trigger from confirmed selection" section to `MainWindow.xaml`/`.xaml.cs` (trigger ID + observed-event text boxes and a button) that calls the already-existing, already-tested `IntegrationEditorViewModel.AddTrigger`, building a `TriggerDefinition` from the currently-confirmed selection with a single `CaptureFieldsAction` referencing the field just added. A user can now go confirm → add field → add trigger → save, using only the real UI.
2. **"Confirm Selection" could not work with a real mouse.** To click the Agent's own Confirm button, the pointer must physically cross onto the Agent's own window. `InspectionController.ApplyResult` excluded the Agent's own process (correctly, to keep the overlay from reporting itself) by publishing `InspectionState.Active(null, null, warning)`, which cleared the previously-displayed snapshot — by the time the pointer (and click) reached Confirm, `CurrentState.Snapshot` was null and `ConfirmAsync()` threw. Fixed by tracking the last real (non-excluded) snapshot/fingerprint in `InspectionController` and reusing it — instead of `null` — specifically for the excluded-own-process branch; a genuine not-found result (timeout, window missing, element unavailable, or nothing under the pointer) still clears the snapshot exactly as before, since that remains correct behavior for moving off any element onto empty desktop. A new regression test, `ConfirmAsync_still_confirms_the_last_real_element_after_the_pointer_crosses_onto_the_agents_own_window` (`InspectionControllerTests.cs`), was written RED-first against the pre-fix code (confirmed the exact `InvalidOperationException` the review predicted) and is GREEN after the fix, without breaking either pre-existing excluded-process test (both still assert `Snapshot` is null, correctly, since neither has a prior real snapshot to fall back to).
3. **Overlay likely misplaced on scaled displays.** `HighlightOverlayWindow.UpdateHighlight` assigned UI Automation's `BoundingRectangle` (physical screen pixels) directly to WPF `Window.Left`/`Top`/`Width`/`Height` (device-independent units, 96 DPI baseline) — wrong on any scaled display (this machine runs 125%, `AppliedDPI 120`). Fixed by converting physical pixels to DIUs before assigning: `PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice` when an HWND already exists (every call after the first `Show()`), falling back to `VisualTreeHelper.GetDpi(this)` for the very first call (before the HWND exists). `OverlayExclusionTests`' `Left == bounds.X` assertion was also circular (comparing WPF-space to UIA-space without proving physical coverage) — replaced with a same-space comparison: the overlay's real HWND rect via a P/Invoke `GetWindowRect` (physical pixels) against the UIA `BoundingRectangle` (also physical pixels). Both `OverlayExclusionTests` pass against the DPI fix on this 125%-scaled machine.

**Explicitly not done in this fix wave:** no human has yet manually clicked through the real `Prescriva.Agent.Desktop.exe` UI end-to-end. The three fixes above address defects found by code review of that path; an actual manual walkthrough by a person with a mouse is still needed before this plan can be considered fully verified — see the Limitations entry below.

Verification commands (as of the final-review fix wave):

```powershell
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release
```

Test counts as of the final-review fix wave (single `dotnet test Prescriva.Agent.slnx --configuration Release` run, all assemblies unblocked this run — no Smart App Control interference hit): Domain 58, Infrastructure 9, Windows.Tests 6, Application.Tests 27 (up from 26 — the 1 new test is the `InspectionController` regression test above), Windows.IntegrationTests 21 (unchanged count; `OverlayExclusionTests`' assertion was changed, not added to). Total 121 passed, 0 failed. `Prescriva.Agent.slnx --configuration Release` built with 0 warnings/0 errors.

## Trigger Runtime, Events and Test Mode plan

Plan: `docs/superpowers/plans/2026-09-27-trigger-runtime-events-and-test-mode.md`, the third and final plan of milestone one. User explicitly chose to proceed to this plan without the Windows Inspector plan's manual click-through (see Limitations above) — that debt is carried forward, not resolved.

Tasks 1–3 of 5 are complete (each task-reviewed, each with a fix round applied and re-reviewed clean):

- **Task 1**: `ITriggerProvider.WatchAsync(TriggerDefinition, CancellationToken) -> IAsyncEnumerable<TriggerSignal>` and `TriggerDeduplicator.Accept(TriggerSignal) -> bool` (Application layer, plain data). `UiAutomationTriggerProvider` (Windows layer) subscribes to real UIA `Invoke` events through `AutomationDispatcher`, unsubscribing cleanly on cancellation or element disappearance. Deduplication keys only on session + trigger ID + native occurrence identity/time window — `TriggerSignal` has exactly 4 fields, no captured-value field exists to accidentally key on, and a reflection test locks this invariant in permanently.
- **Task 2**: `IEventOutbox`/`IPayloadProtector` (Application) + `SqliteEventOutbox`/`DpapiPayloadProtector`/`OutboxSchema`/`RetentionService`/`OutboxCapacityPolicy` (Infrastructure). DPAPI uses `DataProtectionScope.CurrentUser`. Only the serialized `DomainEventPayload` is ciphertext; all metadata (ID/session/sequence/timestamp/type/configuration version) is plain, queryable columns. Real ADO.NET transactions back `AppendAsync`'s atomicity, proven by a test that forces a genuine commit-time failure (a second connection's held SHARED lock blocks the write transaction's commit-time EXCLUSIVE-lock escalation) and checks the row is truly absent via a fresh connection. A corrupted/undecryptable payload is quarantined (status column + reason), not lost or crashing. `RetentionService` deletes confirmed events strictly older than 7 days (keyed on the event's own occurrence timestamp, not confirmation time — an explicit, deliberate reading of an ambiguous spec sentence), keyed off UTC-normalized timestamps.
- **Task 3**: `AgentRuntime.ActivateAsync(IntegrationConfiguration, CancellationToken)` (genuinely unconditional — no approval gate; Task 4 adds that) + `SessionCoordinator` (one per `ApplicationInstance`, serializes operations within an instance's session via a semaphore while different instances progress fully independently; appends events to the outbox *before* publishing a success diagnostic) + `RuntimeDiagnostic` + `IApplicationInstanceSource`/`WindowsApplicationInstanceSource` (plain `Process.GetProcessesByName` polling, no UIA needed) + `ITechnicalLog`/`StructuredTechnicalLog` (JSON-lines; every log call site traced to confirm no captured field value is ever logged, only IDs/codes/provider/confidence/elapsed time — a discipline-backed guarantee, not a structurally-enforced one, since `TechnicalLogEntry.Message`/`Code` are plain strings). A real fake-based test (`AgentRuntimeTests`, no Process/UIA) proves per-instance session independence deterministically, alongside `MultipleInstanceTests` (real two-process integration test, currently unverifiable in this session — see environment note below).

**Prerequisite fix, applied between Task 3 and Task 4 (not a plan task, done standalone):** Task 3's review surfaced a real gap in already-closed Windows Inspector plan code — `AutomationWindowLocator.Find(processIdentity, windowRule)` matched purely by process name and window title, with no per-instance identity, so with two running instances of the same configured application, which physical window got resolved for real field capture was nondeterministic. Fixed: `Find` gained an optional `int? processId` (null preserves prior behavior exactly), threaded through `UiAutomationSelectorResolver`/`UiAutomationTriggerProvider`'s constructors (also optional, backward-compatible) so `AgentRuntime`'s existing `SelectorResolverFactory`/`CaptureProviderFactory` delegates (which already receive `ApplicationInstance.ProcessId`, unused until now) can scope a resolver to one specific instance. No production code wires this yet (only test fakes exist so far) — this closes the capability gap, not a live bug in shipped behavior. Verified by full manual code trace only (see environment note below); a real two-process test exists (`SelectorResolutionTests.cs`) but could not be executed this session.

**Environment note — Smart App Control, worsening:** during Tasks 2/3 and the prerequisite fix, the Smart App Control block (see the Windows Inspector plan's note above and `docs/testing.md`) was reproduced on assemblies with zero UI Automation involvement (`Prescriva.Agent.Domain.dll`, `Prescriva.Agent.Application.dll`) immediately after a one-line edit — it is keyed on "any freshly-modified binary's reputation/hash," not on what the assembly does. During the prerequisite fix, the block became persistent across a full session (did not clear on retry, on a `dotnet build-server shutdown` + clean rebuild, or over several minutes — worse than every prior occurrence, which had cleared within one or two retries). Two pieces of work in this plan (`MultipleInstanceTests`, the prerequisite fix's new `SelectorResolutionTests` cases) are approved on read-by-hand review only and still owe a real execution run once this clears or on a different machine.

(Superseded — see Status at the top.) Next was: Task 4 (test mode and activation gate — `IntegrationTestRunner`, `ConfigurationApproval`, `ConfigurationFingerprint`, `TestModeView`), then Task 5 (end-to-end milestone verification and handoff, the plan's own closing task), and the still-open manual click-through / x86 verification noted above (the Windows Inspector plan's debt) plus a real execution run of everything currently approved by reading alone (above).

Limitations (Windows Inspector plan, still open):
- The "element destroyed during capture" test scenario (Task 3) kills the whole TestTarget process rather than removing a single element from a still-running app/window — a real WPF/UIA limitation (`AutomationPeer.InvalidatePeer()` doesn't reliably produce `ElementNotAvailableException` since the CLR peer object stays alive) made the narrower scenario impractical to construct; accepted as a documented trade-off, not fixed.
- `FieldDefinition` is accepted by `ICaptureProvider.CaptureAsync` but not yet used for any per-field customization or selector/handle-consistency validation — capture behavior is currently identical regardless of the field passed in.
- `CaptureResult.Confidence` is currently always a hardcoded `1.0` (success) or `0` (failure), carrying no signal beyond the outcome already present.
- `FindApplicationWindow`/`AutomationWindowLocator.Find` enumerate every top-level desktop window and call `Process.GetProcessById` per window — O(all top-level windows) per call, unoptimized but not a correctness issue at current scale.
- `InspectionController.ObservePointerAsync` has no guard against being called after `StopAsync` (no check on the current status) — a stray call after stop could silently resume producing `Active` states; not covered by required scenarios, likely a non-issue given intended caller usage from a ViewModel.
- `HighlightOverlayWindow` uses `GetWindowLong`/`SetWindowLong` (32-bit, not the `Ptr` variants) for `GWL_EXSTYLE` — correct since the style is always a 32-bit bitmask on both x86/x64, but worth a comment if touched again, since it's a common source of P/Invoke review confusion.
- (Resolved 2026-10-04: both pass on Windows CI.) The 2 `OverlayExclusionTests` could not be re-verified after Task 4's fix round due to the Smart App Control issue above (the fix round's only change to that file was a comment; no production-code reason to suspect a regression, but this is inference, not a fresh green run) — worth an explicit re-run once the environment issue is resolved or on a different machine.
- (Task 5) `IntegrationEditorViewModel`'s `_resolvedHandles` cache is never invalidated by `ReloadAsync`/`AddField`/`SaveAsync` beyond an explicit `_resolvedHandles.Clear()` in `CreateIntegration`/`ReloadAsync` — if a field is ever removed or re-added with the same semantic ID after a resolve (no "remove field" API exists yet, so not currently reachable), a stale handle could linger. Not a defect against any current capability, but worth a guard if field removal is ever added.
- (Task 5) `MainWindow`'s pointer-inspection loop polls `GetCursorPos` on a fixed 80ms `DispatcherTimer` rather than reacting to real mouse-move events (e.g. a low-level mouse hook) — simple and sufficient for a minimal vertical slice, but coarser and slightly laggier than a true event-driven approach; acceptable trade-off for scope, not revisited.
- (Task 5) `Prescriva.Agent.Application`'s `InternalsVisibleTo` was widened to include `Prescriva.Agent.Application.Tests` (previously only `Prescriva.Agent.Windows`) so `ResolvedElementHandle` could be subclassed by a test-only fake for `IntegrationEditorViewModelTests`' resolve/read-value tests. The type remains unconstructable from any other assembly, including `Prescriva.Agent.Desktop` and any other test project — but this is a real (if narrow) widening of the opacity boundary Task 3 established, worth being aware of if that boundary matters again in a future review.
- (Task 5) Manual, human-in-the-loop verification of `Prescriva.Agent.Desktop.exe`'s own compiled WPF UI (clicking its buttons, watching the highlight overlay track the mouse) was not performed in this session (no mouse/keyboard interaction available) — see `docs/testing/windows-inspector-manual.md` for exactly what was and was not verified, and the real automated test that covers the underlying pipeline instead. No x86 sample application exists in this repository, so the brief's x86 repeat-verification step is an open gap, not performed.
- (Final-review fix wave) Controls without a stable `AutomationId` currently cannot be resolved at all: `ElementFingerprint` carries no ancestors/labels/relative-position signals (per spec §6), so any control lacking a stable `AutomationId` scores below the ambiguity threshold and `SelectorMatcher`/`UiAutomationSelectorResolver` always report it `NotFound`, regardless of how uniquely a human could otherwise identify it on screen. Deferred to the next plan.
- (Resolved 2026-10-04 by `ProcessIdentity`.) (Final-review fix wave) `ApplicationDefinition.ProcessIdentity` (as typed into `MainWindow`'s "Target process" box, e.g. `Prescriva.Agent.TestTarget.exe`) is never cross-validated against what `AutomationWindowLocator` actually compares — a bare `Process.ProcessName` (no `.exe` suffix). Nothing currently catches a configuration whose `ProcessIdentity` has (or is missing) the `.exe` suffix mismatching the locator's comparison; it would simply never resolve, with no diagnostic pointing at the mismatch specifically. Deferred to the next plan.
- (Final-review fix wave) The single dispatcher STA thread (`AutomationDispatcher`) has no recovery path if a queued COM call wedges — e.g. inspecting a hung x86 process via UIA can block the STA thread indefinitely. Every later operation queued to it would then time out (per-call timeout still applies) but the thread itself never recovers, so every subsequent call times out too, forever, until the process is restarted. `AutomationDispatcher.Dispose()` also has a latent unhandled-exception path: it `Join`s the thread with a 5s timeout, and if the thread is still stuck when the queue is disposed after that `Join` returns, disposing the queue while the stuck thread may still reference it is not guaranteed safe. Neither issue is covered by any current test (both require a genuinely wedged COM call to reproduce). Deferred to the next plan.
- (Partly resolved 2026-10-04: `DesktopWalkthroughTests` now drives the compiled Desktop UI with the real cursor on CI; only the visual checks remain for a person.) **No human has yet manually clicked through the real `Prescriva.Agent.Desktop.exe` UI end-to-end.** The final-review fix wave (trigger UI, Confirm-with-a-real-mouse, DPI-aware overlay) addresses defects found by code review and fake/real-UIA-backed automated tests against that path, but an actual manual walkthrough by a person with a mouse — launching `Prescriva.Agent.Desktop.exe`, creating an integration, adding a stage, inspecting a real target, confirming a selection, adding a field and a trigger, saving, reloading, resolving and reading a value, all by hand — is still needed before this plan can be considered fully verified. Called out explicitly for whoever picks this up next.

Limitations (foundation plan, still open — carried forward, see the original list below for full detail): `DomainEvent.ConfigurationVersion` conflates schema version with content revision; `SessionEngine`'s field-lookup relies on an upstream validator invariant; `SessionFailure.CaptureFailure` naming clash with its enum; untyped `FileNotFoundException` on missing-file load; case-sensitive ID uniqueness in `ConfigurationValidator`; `SelectorWeights` not persisted alongside saved configurations.

The Windows Inspector, UI Automation capture and minimal WPF configurator vertical slice are now implemented (this plan's Tasks 1–5, above), and this plan's final whole-branch review and its fix wave are also complete. The trigger runtime and SQLite/DPAPI event persistence belong to a further, not-yet-started plan; a human-driven manual click-through of `Prescriva.Agent.Desktop.exe`'s own UI (see `docs/testing/windows-inspector-manual.md`) and x86 verification also remain open. The first milestone is not yet complete until that further plan (and the still-open manual click-through) are done.

Additional still-open items, tracked only in this session's (gitignored) `.superpowers/` task notes until now:

- `DomainEvent.ConfigurationVersion` currently holds the configuration's schema version (always `1`), not a per-edit content revision, so two different revisions of the same configuration produce events that cannot be distinguished from each other. This must be resolved (rename the field, or add a real revision/hash) before a future plan persists events durably (e.g. to SQLite) and needs to reconstruct or deduplicate by exact configuration revision.
- `SessionEngine`'s field-lookup for a `CaptureFieldsAction`'s field ID assumes the referenced field always exists in `configuration.Fields` (guaranteed today only because the constructor validates the configuration first via `ConfigurationValidator`); if that invariant is ever broken by a future configuration-model change, the lookup will throw instead of producing a typed failure.
- `SessionFailure.CaptureFailure` (a record property) shares its name with the `CaptureFailure` enum type it holds, which reads awkwardly at call sites; a rename (e.g. to `CaptureFailureKind`) would be clearer but is cosmetic.
- The JSON configuration store's `LoadAsync` throws an untyped `FileNotFoundException` on a missing file rather than a typed "not found" error; a future plan that adds load/reload behavior should introduce a typed failure for this.
- `ConfigurationValidator`'s ID-uniqueness checks are case-sensitive, so two field/action IDs differing only in case are treated as distinct even though they may collide as string keys elsewhere; worth revisiting if this proves confusing in practice.
- `SelectorWeights` carries a `Version` in code, but nothing records which weights version resolved a selector or was in effect when a configuration was saved; a future plan may need to persist this alongside saved configurations for reproducibility.
