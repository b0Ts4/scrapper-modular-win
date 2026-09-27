# Prescriva Agent Trigger Runtime, Events and Test Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Completar o milestone detectando gatilhos, executando etapas, persistindo eventos protegidos e exibindo diagnósticos no modo de teste.

**Architecture:** Um coordenador de sessão consome ocorrências de providers de gatilho, resolve e captura campos, aplica a máquina de estados e grava eventos numa outbox. Desktop observa diagnósticos e eventos por contratos Application, sem acessar UIA ou SQLite diretamente.

**Tech Stack:** C# 14, .NET 10 WPF, UI Automation events, Microsoft.Data.Sqlite, Windows DPAPI, xUnit

**Spec:** `docs/superpowers/specs/2026-09-27-prescriva-agent-milestone-1-design.md`

## Global Constraints

- Somente configurações testadas podem ser ativadas.
- Eventos têm ID único, sessão, sequência, timestamp e versão da configuração.
- Payloads SQLite são protegidos por DPAPI CurrentUser; logs não contêm valores capturados.
- Eventos confirmados são retidos sete dias; pendentes permanecem até confirmação ou ação explícita.
- Nenhum evento válido é emitido quando falta campo obrigatório.

## Review Focus

- Duplo clique ou evento UIA duplicado deve gerar um evento lógico, coberto na Task 1.
- Queda entre gravação e retorno não pode perder nem duplicar evento, coberto na Task 2.
- Payload que não pode ser descriptografado deve ficar em quarentena, coberto na Task 2.
- Duas instâncias do TestTarget devem manter sessões separadas, coberto na Task 3.
- Configuração alterada após teste deve perder o estado aprovado, coberto na Task 4.

---

### Task 1: Trigger provider and deduplication

**Files:**
- Create: `src/Prescriva.Agent.Application/Triggers/ITriggerProvider.cs`
- Create: `src/Prescriva.Agent.Application/Triggers/TriggerSignal.cs`
- Create: `src/Prescriva.Agent.Application/Triggers/TriggerDeduplicator.cs`
- Create: `src/Prescriva.Agent.Windows/Automation/UiAutomationTriggerProvider.cs`
- Test: `tests/Prescriva.Agent.Application.Tests/Triggers/TriggerDeduplicatorTests.cs`
- Test: `tests/Prescriva.Agent.Windows.IntegrationTests/Automation/TriggerProviderTests.cs`

**Interfaces:**
- Consumes: resolved trigger fingerprints from the Inspector plan.
- Produces: `ITriggerProvider.WatchAsync(TriggerDefinition, CancellationToken) -> IAsyncEnumerable<TriggerSignal>`; `TriggerDeduplicator.Accept(TriggerSignal) -> bool`.

- [ ] **Step 1: Write failing tests**

Cover invoke events for Add/Finish, element disappearance, cancellation, two signals inside the deduplication window, and distinct triggers inside that same window.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test Prescriva.Agent.slnx --filter "FullyQualifiedName~Trigger"`
Expected: compile failure for missing trigger contracts.

- [ ] **Step 3: Implement provider and deduplicator**

Subscribe and unsubscribe UIA handlers on the automation dispatcher. Deduplicate by session, trigger ID and native occurrence identity/time window, never by payload value.

- [ ] **Step 4: Confirm GREEN and commit**

Run the Task 1 command; expect PASS. Run `git add src tests`, then `git commit -m "feat: observe configured UI Automation triggers"`.

### Task 2: Encrypted SQLite event outbox

**Files:**
- Create: `src/Prescriva.Agent.Application/Events/IEventOutbox.cs`
- Create: `src/Prescriva.Agent.Application/Security/IPayloadProtector.cs`
- Create: `src/Prescriva.Agent.Infrastructure/Security/DpapiPayloadProtector.cs`
- Create: `src/Prescriva.Agent.Infrastructure/Events/SqliteEventOutbox.cs`
- Create: `src/Prescriva.Agent.Infrastructure/Events/OutboxSchema.cs`
- Create: `src/Prescriva.Agent.Infrastructure/Events/RetentionService.cs`
- Create: `src/Prescriva.Agent.Infrastructure/Events/OutboxCapacityPolicy.cs`
- Test: `tests/Prescriva.Agent.Infrastructure.Tests/Security/DpapiPayloadProtectorTests.cs`
- Test: `tests/Prescriva.Agent.Infrastructure.Tests/Events/SqliteEventOutboxTests.cs`
- Test: `tests/Prescriva.Agent.Infrastructure.Tests/Events/RetentionServiceTests.cs`
- Test: `tests/Prescriva.Agent.Infrastructure.Tests/Events/OutboxCapacityPolicyTests.cs`

**Interfaces:**
- Consumes: `DomainEvent` from foundation plan.
- Produces: atomic `IEventOutbox.AppendAsync`, `ReadPendingAsync`, `MarkConfirmedAsync`, `QuarantineAsync` and `DeleteConfirmedBeforeAsync`; `IPayloadProtector.Protect/Unprotect`.

- [ ] **Step 1: Write failing security and outbox tests**

Assert plaintext never appears in the database bytes, round-trip works for CurrentUser, duplicate event ID is idempotent, restart preserves order, corrupt ciphertext is quarantined, atomic failure leaves no partial row, retention deletes only confirmed events older than seven days, and capacity pressure alerts without deleting pending events.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test tests/Prescriva.Agent.Infrastructure.Tests --filter "FullyQualifiedName~PayloadProtector|FullyQualifiedName~Outbox|FullyQualifiedName~Retention"`
Expected: compile failure for missing outbox types.

- [ ] **Step 3: Implement DPAPI, schema migration v1 and transactions**

Store searchable metadata in columns and only the serialized business payload as ciphertext. Use a unique event ID and sequence constraint per session. Do not log exception data containing plaintext.

- [ ] **Step 4: Confirm GREEN and commit**

Run the Task 2 command; expect PASS. Run `git add src tests`, then `git commit -m "feat: persist encrypted domain event outbox"`.

### Task 3: Runtime session coordinator

**Files:**
- Create: `src/Prescriva.Agent.Application/Runtime/AgentRuntime.cs`
- Create: `src/Prescriva.Agent.Application/Runtime/SessionCoordinator.cs`
- Create: `src/Prescriva.Agent.Application/Runtime/RuntimeDiagnostic.cs`
- Create: `src/Prescriva.Agent.Application/Runtime/IApplicationInstanceSource.cs`
- Create: `src/Prescriva.Agent.Application/Diagnostics/ITechnicalLog.cs`
- Create: `src/Prescriva.Agent.Infrastructure/Diagnostics/StructuredTechnicalLog.cs`
- Create: `src/Prescriva.Agent.Windows/Processes/WindowsApplicationInstanceSource.cs`
- Test: `tests/Prescriva.Agent.Application.Tests/Runtime/SessionCoordinatorTests.cs`
- Test: `tests/Prescriva.Agent.Infrastructure.Tests/Diagnostics/StructuredTechnicalLogTests.cs`
- Test: `tests/Prescriva.Agent.Windows.IntegrationTests/Runtime/MultipleInstanceTests.cs`

**Interfaces:**
- Consumes: trigger provider, selector resolver, capture provider, `SessionEngine`, outbox and application instance source.
- Produces: `AgentRuntime.ActivateAsync(IntegrationConfiguration, CancellationToken)`; observable diagnostics with codes, severity, selector confidence, provider and elapsed time.

- [ ] **Step 1: Write failing orchestration tests**

Cover Add capturing required fields and persisting `item_added`, missing required field preventing append, fallback diagnostic, Finish emitting `budget_finished`, application close ending session, two process instances maintaining independent sequences, and technical logs excluding captured values while retaining IDs, codes and timings.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test Prescriva.Agent.slnx --filter "FullyQualifiedName~SessionCoordinator|FullyQualifiedName~MultipleInstance"`
Expected: compile failure for missing runtime.

- [ ] **Step 3: Implement the coordinator**

Create one session per process instance. Serialize operations inside each session while allowing different sessions to progress independently. Append emitted events before publishing success diagnostics.

- [ ] **Step 4: Confirm GREEN and commit**

Run the Task 3 command; expect PASS. Run `git add src tests`, then `git commit -m "feat: coordinate capture sessions and events"`.

### Task 4: Test mode and activation gate

**Files:**
- Create: `src/Prescriva.Agent.Application/Testing/IntegrationTestRunner.cs`
- Create: `src/Prescriva.Agent.Application/Testing/IntegrationTestReport.cs`
- Create: `src/Prescriva.Agent.Domain/Configuration/ConfigurationApproval.cs`
- Create: `src/Prescriva.Agent.Application/Testing/ConfigurationFingerprint.cs`
- Create: `src/Prescriva.Agent.Desktop/Testing/TestModeView.xaml`
- Create: `src/Prescriva.Agent.Desktop/Testing/TestModeView.xaml.cs`
- Create: `src/Prescriva.Agent.Desktop/Testing/TestModeViewModel.cs`
- Test: `tests/Prescriva.Agent.Application.Tests/Testing/IntegrationTestRunnerTests.cs`
- Test: `tests/Prescriva.Agent.Application.Tests/Testing/TestModeViewModelTests.cs`

**Interfaces:**
- Consumes: runtime contracts and immutable hash of validated configuration.
- Produces: report entries for fields, triggers, transitions and events; approval bound to exact configuration hash; activation returns `NotTested` when absent or stale.

- [ ] **Step 1: Write failing test-mode and gate tests**

Cover found/not-found/ambiguous fields, provider and confidence display, trigger wait/detection, successful approval, failed test rejection and any configuration edit invalidating approval.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test tests/Prescriva.Agent.Application.Tests --filter "FullyQualifiedName~IntegrationTestRunner|FullyQualifiedName~TestModeViewModel"`
Expected: compile failure for missing test-mode types.

- [ ] **Step 3: Implement runner, report and WPF view**

Keep captured test values in memory unless the user explicitly saves the run. Render failure codes with actionable Portuguese text while technical logs keep only IDs and metadata.

- [ ] **Step 4: Confirm GREEN and commit**

Run the Task 4 command; expect PASS. Run `git add src tests`, then `git commit -m "feat: add integration test mode and activation gate"`.

### Task 5: End-to-end milestone verification and handoff

**Files:**
- Create: `tests/Prescriva.Agent.Windows.IntegrationTests/EndToEnd/MilestoneFlowTests.cs`
- Create: `docs/architecture/overview.md`
- Create: `docs/architecture/capture-engine.md`
- Create: `docs/architecture/selector-engine.md`
- Create: `docs/architecture/event-engine.md`
- Create: `docs/architecture/security.md`
- Create: `docs/roadmap.md`
- Create: `docs/testing/milestone-1-manual.md`
- Modify: `README.md`
- Modify: `docs/testing.md`
- Modify: `docs/handoffs/current-state.md`
- Modify: `docs/plans/current-plan.md`

**Interfaces:**
- Consumes: every component from all three plans.
- Produces: verified milestone, reproducible documentation and exact next action.

- [ ] **Step 1: Write the failing end-to-end test**

Launch TestTarget, create and reload a configuration, resolve fields/buttons, activate a tested configuration, enter two medicines, observe two ordered `item_added` events, finish, observe `budget_finished`, restart the outbox and assert all three encrypted events remain ordered.

- [ ] **Step 2: Confirm RED, close integration gaps, then confirm GREEN**

Run: `dotnet test tests/Prescriva.Agent.Windows.IntegrationTests --filter FullyQualifiedName~MilestoneFlowTests`
Expected first: FAIL at the first missing integration; expected final: PASS.

- [ ] **Step 3: Run the manual acceptance script**

Exercise selection, overlay, save/restart, moved layout, ambiguity, missing field, Add, Finish, stored events and visible monitoring state on an interactive Windows 10 or 11 desktop. Record environment and results.

- [ ] **Step 4: Update architecture, setup, roadmap and handoff**

Document actual interfaces and limitations. `current-state.md` must state status, last/current task, working and non-working behavior, known issues, important files, decisions, test evidence, active plan and exact next action.

- [ ] **Step 5: Run final verification**

Run: `dotnet restore Prescriva.Agent.slnx`

Run: `dotnet build Prescriva.Agent.slnx --configuration Release --no-restore`

Run: `dotnet test Prescriva.Agent.slnx --configuration Release --no-build`

Expected: every command exits 0, build has zero warnings and all automated tests pass. Manual-only tests must be explicitly identified with recorded results.

- [ ] **Step 6: Commit**

Run: `git add .`

Run: `git commit -m "feat: complete Prescriva Agent milestone one"`.

