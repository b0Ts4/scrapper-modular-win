# Prescriva Agent Configuration Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tornar a configuração sustentável no uso real: aprovações persistidas (sem retestar a cada reinício, mas invalidadas por qualquer edição) e edição da configuração no configurador (remover/alterar campos, gatilhos e etapas) sem nunca produzir uma configuração inválida.

**Architecture:** Aprovação continua sendo dado de Domain (`ConfigurationApproval`) ligado ao hash do conteúdo (`ConfigurationFingerprint`). Um contrato Application (`IApprovalStore`) e um serviço (`ApprovalService`) decidem se existe aprovação válida para o conteúdo atual; Infrastructure persiste em JSON ao lado das configurações. A edição fica em `IntegrationEditorViewModel` (testável com fakes); o `MainWindow` só liga botões.

**Tech Stack:** C# 14, .NET 10, WPF, System.Text.Json, xUnit, UI Automation (testes de walkthrough)

**Predecessor:** `2026-09-27-trigger-runtime-events-and-test-mode.md` (milestone 1, PR #2).

## Global Constraints

- Somente configurações testadas podem ser ativadas — a persistência não pode enfraquecer isso: uma aprovação só vale para o hash exato do conteúdo atual.
- Aprovação persistida não contém valores capturados (só ID da configuração, hash e data).
- Uma edição nunca deixa a configuração inválida em memória: remoções que deixariam referências quebradas são recusadas com mensagem acionável.
- Domain sem dependência de Windows/WPF/JSON.

## Review Focus

- Editar e desfazer a edição (conteúdo volta ao hash aprovado) deve voltar a permitir ativação; qualquer outra edição deve recusar — Task 2.
- Arquivo de aprovação corrompido ou de outra configuração não pode aprovar nada — Task 1.
- Remover um campo usado por um gatilho deve ser recusado, não "consertado" silenciosamente — Task 3.
- Reiniciar o Agent com configuração aprovada e inalterada ativa sem retestar, comprovado pelo Desktop real — Task 5.

---

### Task 1: Persisted approval store

**Files:**
- Create: `src/Prescriva.Agent.Application/Configuration/IApprovalStore.cs`
- Create: `src/Prescriva.Agent.Infrastructure/Configuration/JsonApprovalStore.cs`
- Test: `tests/Prescriva.Agent.Infrastructure.Tests/Configuration/JsonApprovalStoreTests.cs`

**Interfaces:**
- Produces: `IApprovalStore.SaveAsync(ConfigurationApproval)`, `LoadAsync(configurationId) -> ConfigurationApproval?`, `DeleteAsync(configurationId)`.

- [ ] **Step 1: Write failing tests** — round trip; missing file returns null; corrupt JSON returns null (never throws, never approves); a file whose configuration ID differs from the requested one returns null; atomic overwrite; IDs that are not safe file names are rejected.
- [ ] **Step 2: Confirm RED** — `dotnet test tests/Prescriva.Agent.Infrastructure.Tests --filter FullyQualifiedName~ApprovalStore`; expected: compile failure.
- [ ] **Step 3: Implement** — `approvals/<id>.approval.json`, temp-file + move like `JsonConfigurationStore`.
- [ ] **Step 4: GREEN and commit** — `feat: persist configuration approvals`.

### Task 2: Approval service (valid-for-current-content decision)

**Files:**
- Create: `src/Prescriva.Agent.Application/Testing/ApprovalService.cs`
- Test: `tests/Prescriva.Agent.Application.Tests/Testing/ApprovalServiceTests.cs`

**Interfaces:**
- Consumes: `IApprovalStore`, `IntegrationTestReport`, `ConfigurationFingerprint`.
- Produces: `RecordAsync(IntegrationTestReport) -> ConfigurationApproval` (throws for a failed report); `GetStatusAsync(IntegrationConfiguration) -> ApprovalStatus { Approval?, State: NotTested | Approved | ChangedSinceTest }`.

- [ ] **Step 1: Write failing tests** — no approval → NotTested; passing report recorded → Approved after a "restart" (new service instance, same store); any edit → ChangedSinceTest with no usable approval; reverting the edit → Approved again; failed report cannot be recorded.
- [ ] **Step 2–4: RED, implement, GREEN, commit** — `feat: decide approval validity for the current configuration content`.

### Task 3: Editable configuration in the editor view model

**Files:**
- Modify: `src/Prescriva.Agent.Desktop/Configuration/IntegrationEditorViewModel.cs`
- Test: `tests/Prescriva.Agent.Application.Tests/Configuration/IntegrationEditorViewModelTests.cs`

**Interfaces:**
- Produces: `RemoveField(id)`, `UpdateField(id, meaning, required)`, `RemoveTrigger(id)`, `ReplaceTriggerActions(id, actions)`, `RemoveStage(id)`; duplicates rejected on `AddField`/`AddTrigger`/`AddStage`.

- [ ] **Step 1: Write failing tests** — each operation edits and marks unsaved; removing a field referenced by a trigger's capture action is refused naming the trigger; removing a stage still used by a field/trigger/transition is refused; unknown IDs are refused; duplicate IDs refused on add; removing a field drops its cached resolved handle (closes the plan-2 limitation).
- [ ] **Step 2–4: RED, implement, GREEN, commit** — `feat: edit and remove fields, triggers and stages`.

### Task 4: Desktop wiring

**Files:**
- Modify: `src/Prescriva.Agent.Desktop/MainWindow.xaml(.cs)`
- Modify: `src/Prescriva.Agent.Desktop/Testing/TestModeView.xaml.cs` (approve through the service)

- [ ] Approve records the approval through `ApprovalService`; Activate uses the stored approval for the current content; a visible approval state ("Aprovada em …", "Não testada", "Alterada desde o último teste").
- [ ] Field/trigger lists with Remove, Update field (meaning/required) and Replace trigger actions; refusals shown in the status text.
- [ ] Commit — `feat: approval state and configuration editing in the configurator`.

### Task 5: End-to-end verification and handoff

**Files:**
- Create: `tests/Prescriva.Agent.Windows.IntegrationTests/EndToEnd/DesktopConfigurationLifecycleTests.cs`
- Modify: docs (`testing.md`, `architecture/event-engine.md`, `roadmap.md`, `handoffs/current-state.md`, `plans/current-plan.md`)

- [ ] **Step 1: Failing walkthrough** — configure, test, approve; restart the Agent; reload; approval state shows approved and Activate monitors without retesting; stop; edit (make `quantity` optional) → state "Alterada…", Activate refused; revert → approved again; remove the Finish trigger and save → activation refused until retested.
- [ ] **Step 2: GREEN on Windows CI**, update docs and handoff, commit `docs: record configuration lifecycle verification`.
