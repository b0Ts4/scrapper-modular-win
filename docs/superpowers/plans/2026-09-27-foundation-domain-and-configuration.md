# Prescriva Agent Foundation, Domain and Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Criar uma solution compilável com o domínio, configuração declarativa, seletores puros e máquina de sessões do primeiro milestone.

**Architecture:** Domain contém somente tipos e regras determinísticas; Application define portas e orquestra casos de uso sem depender de Windows. Serialização JSON fica em Infrastructure e valida antes de materializar uma integração ativa.

**Tech Stack:** C# 14, .NET 10 LTS, xUnit, System.Text.Json

**Spec:** `docs/superpowers/specs/2026-09-27-prescriva-agent-milestone-1-design.md`

## Global Constraints

- Suportar somente Windows 10/11 x64 no runtime; Domain e Application usam `net10.0`.
- Capturar somente elementos explicitamente configurados.
- Configurações usam `schemaVersion: 1` e não armazenam valores capturados.
- Domain não referencia WPF, UI Automation, SQLite ou DPAPI.
- Todo comportamento determinístico segue RED → GREEN → REFACTOR.

## Review Focus

- JSON com versão desconhecida deve ser rejeitado com erro legível, coberto na Task 3.
- Dois candidatos quase empatados devem resultar em `Ambiguous`, coberto na Task 2.
- Campo obrigatório ausente deve impedir evento, coberto na Task 4.
- Gatilho recebido fora da etapa corrente deve ser ignorado, coberto na Task 4.
- IDs semânticos duplicados devem invalidar a configuração, coberto na Task 3.

---

### Task 1: Solution, conventions and repository guidance

**Files:**
- Create: `Prescriva.Agent.slnx`
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `.editorconfig`
- Create: `.gitignore`
- Create: `src/Prescriva.Agent.Domain/Prescriva.Agent.Domain.csproj`
- Create: `src/Prescriva.Agent.Application/Prescriva.Agent.Application.csproj`
- Create: `src/Prescriva.Agent.Infrastructure/Prescriva.Agent.Infrastructure.csproj`
- Create: `tests/Prescriva.Agent.Domain.Tests/Prescriva.Agent.Domain.Tests.csproj`
- Create: `tests/Prescriva.Agent.Application.Tests/Prescriva.Agent.Application.Tests.csproj`
- Create: `tests/Prescriva.Agent.Infrastructure.Tests/Prescriva.Agent.Infrastructure.Tests.csproj`
- Create: `README.md`
- Create: `AGENTS.md`
- Create: `docs/setup.md`
- Create: `docs/testing.md`
- Create: `docs/handoffs/current-state.md`

**Interfaces:**
- Consumes: .NET 10 SDK installed and available as `dotnet`.
- Produces: buildable solution and mandatory contributor workflow.

- [ ] **Step 1: Verify the required SDK**

Run: `dotnet --version`
Expected: version starts with `10.`. If unavailable, install the .NET 10 SDK before modifying project files.

- [ ] **Step 2: Scaffold the solution and projects**

Use SDK templates, target `net10.0`, enable nullable and warnings as errors, add project references `Application -> Domain` and `Infrastructure -> Application, Domain`, then add all projects to `Prescriva.Agent.slnx`.

- [ ] **Step 3: Write repository guidance**

`AGENTS.md` must reproduce the required Superpowers order, privacy rules, build/test commands, documentation duties and Definition of Done from the approved spec. `current-state.md` records that Task 1 is complete and Task 2 is next.

- [ ] **Step 4: Verify the empty baseline**

Run: `dotnet build Prescriva.Agent.slnx --configuration Release`
Expected: build succeeds with zero warnings.

Run: `dotnet test Prescriva.Agent.slnx --configuration Release --no-build`
Expected: all template tests pass.

- [ ] **Step 5: Commit**

Run: `git add .`

Run: `git commit -m "build: scaffold Prescriva Agent solution"`

### Task 2: Element fingerprints and selector scoring

**Files:**
- Create: `src/Prescriva.Agent.Domain/Selectors/ElementFingerprint.cs`
- Create: `src/Prescriva.Agent.Domain/Selectors/AncestorFingerprint.cs`
- Create: `src/Prescriva.Agent.Domain/Selectors/ElementCandidate.cs`
- Create: `src/Prescriva.Agent.Domain/Selectors/SelectorWeights.cs`
- Create: `src/Prescriva.Agent.Domain/Selectors/SelectorMatch.cs`
- Create: `src/Prescriva.Agent.Domain/Selectors/SelectorMatcher.cs`
- Test: `tests/Prescriva.Agent.Domain.Tests/Selectors/SelectorMatcherTests.cs`

**Interfaces:**
- Consumes: no external interfaces.
- Produces: `SelectorMatcher.Match(ElementFingerprint selector, IReadOnlyList<ElementCandidate> candidates, SelectorWeights weights) -> SelectorMatch`; `SelectorMatchStatus` values `Found`, `NotFound`, `Ambiguous`.

- [ ] **Step 1: Write failing scoring tests**

Add tests named `ExactAutomationIdAndControlType_ReturnsFound`, `PositionOnly_ReturnsNotFound`, `CloseTopScores_ReturnsAmbiguous`, `DifferentProcess_IsExcluded`, and `MissingOptionalSignals_DoesNotThrow`. Assert status, selected candidate ID, score range and evidence keys.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test tests/Prescriva.Agent.Domain.Tests --filter FullyQualifiedName~SelectorMatcherTests`
Expected: compile failure because selector types do not exist.

- [ ] **Step 3: Implement immutable selector types and matcher**

Use normalized ordinal case-insensitive text comparison. Hard filters are process identity and window rule. Strong signals outweigh ancestry, neighbors and relative bounds. `Ambiguous` is returned when the best result clears the minimum but its lead is below `MinimumLead`.

- [ ] **Step 4: Confirm GREEN and refactor**

Run: `dotnet test tests/Prescriva.Agent.Domain.Tests --filter FullyQualifiedName~SelectorMatcherTests`
Expected: all selector tests pass.

- [ ] **Step 5: Commit**

Run: `git add src/Prescriva.Agent.Domain/Selectors tests/Prescriva.Agent.Domain.Tests/Selectors`

Run: `git commit -m "feat: add resilient selector scoring"`

### Task 3: Versioned declarative integration configuration

**Files:**
- Create: `src/Prescriva.Agent.Domain/Configuration/IntegrationConfiguration.cs`
- Create: `src/Prescriva.Agent.Domain/Configuration/ApplicationDefinition.cs`
- Create: `src/Prescriva.Agent.Domain/Configuration/FieldDefinition.cs`
- Create: `src/Prescriva.Agent.Domain/Configuration/StageDefinition.cs`
- Create: `src/Prescriva.Agent.Domain/Configuration/TriggerDefinition.cs`
- Create: `src/Prescriva.Agent.Domain/Configuration/TriggerActionDefinition.cs`
- Create: `src/Prescriva.Agent.Domain/Configuration/ConfigurationValidationResult.cs`
- Create: `src/Prescriva.Agent.Domain/Configuration/ConfigurationValidator.cs`
- Create: `src/Prescriva.Agent.Application/Configuration/IConfigurationStore.cs`
- Create: `src/Prescriva.Agent.Infrastructure/Configuration/JsonConfigurationStore.cs`
- Test: `tests/Prescriva.Agent.Domain.Tests/Configuration/ConfigurationValidatorTests.cs`
- Test: `tests/Prescriva.Agent.Infrastructure.Tests/Configuration/JsonConfigurationStoreTests.cs`

**Interfaces:**
- Consumes: `ElementFingerprint` from Task 2.
- Produces: `IConfigurationStore.LoadAsync(string id, CancellationToken) -> Task<IntegrationConfiguration>` and `SaveAsync(IntegrationConfiguration, CancellationToken) -> Task`; schema version constant `1`.

- [ ] **Step 1: Write failing validation and round-trip tests**

Cover a valid two-stage integration, duplicate field/trigger/stage IDs, missing action references, unknown `schemaVersion`, malformed JSON and save/load equality. Assert errors contain stable codes rather than matching prose.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test Prescriva.Agent.slnx --filter "FullyQualifiedName~Configuration"`
Expected: compile failure for missing configuration types.

- [ ] **Step 3: Implement records, validation and JSON store**

Use discriminated action records `CaptureFieldsAction`, `TransitionStageAction`, `EmitEventAction`, `ClearStateAction`, `FinishSessionAction`, and `CancelSessionAction`. Save atomically via temporary file plus replace; reject activation when validation fails.

- [ ] **Step 4: Confirm GREEN**

Run: `dotnet test Prescriva.Agent.slnx --filter "FullyQualifiedName~Configuration"`
Expected: all configuration tests pass.

- [ ] **Step 5: Commit**

Run: `git add src tests`

Run: `git commit -m "feat: add versioned integration configuration"`

### Task 4: Session state machine and domain events

**Files:**
- Create: `src/Prescriva.Agent.Domain/Sessions/CaptureSession.cs`
- Create: `src/Prescriva.Agent.Domain/Sessions/SessionState.cs`
- Create: `src/Prescriva.Agent.Domain/Sessions/TriggerOccurrence.cs`
- Create: `src/Prescriva.Agent.Domain/Sessions/CapturedFieldValue.cs`
- Create: `src/Prescriva.Agent.Domain/Events/DomainEvent.cs`
- Create: `src/Prescriva.Agent.Domain/Sessions/SessionTransitionResult.cs`
- Create: `src/Prescriva.Agent.Domain/Sessions/SessionEngine.cs`
- Test: `tests/Prescriva.Agent.Domain.Tests/Sessions/SessionEngineTests.cs`

**Interfaces:**
- Consumes: validated stage, trigger and action definitions from Task 3.
- Produces: `SessionEngine.Apply(CaptureSession session, TriggerOccurrence occurrence, IReadOnlyDictionary<string, CapturedFieldValue> values, DateTimeOffset now) -> SessionTransitionResult`.

- [ ] **Step 1: Write failing state transition tests**

Cover multiple triggers for one action, stage advance/back, trigger outside current stage, missing required field, monotonically increasing event sequence, cancel clearing state and finish emitting `budget_finished`.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test tests/Prescriva.Agent.Domain.Tests --filter FullyQualifiedName~SessionEngineTests`
Expected: compile failure because session types do not exist.

- [ ] **Step 3: Implement the pure state machine**

Return a new immutable session, emitted events and typed failures. Do not read the clock or generate GUIDs internally; accept all nondeterministic values from the caller.

- [ ] **Step 4: Confirm GREEN and full-plan verification**

Run: `dotnet test Prescriva.Agent.slnx --configuration Release`
Expected: all tests pass.

Run: `dotnet build Prescriva.Agent.slnx --configuration Release --no-restore`
Expected: zero warnings and zero errors.

- [ ] **Step 5: Update handoff and commit**

Record completed capabilities, test commands and next plan in `docs/handoffs/current-state.md`.

Run: `git add .`

Run: `git commit -m "feat: add capture session state machine"`

