# Prescriva Agent Windows Inspector and Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permitir selecionar, destacar, reencontrar e ler controles reais do TestTarget usando UI Automation.

**Architecture:** Windows traduz objetos nativos em snapshots neutros consumidos por Application e Domain. Uma thread STA dedicada possui todo acesso ao UI Automation; Desktop apenas apresenta estado e envia comandos.

**Tech Stack:** C# 14, .NET 10 WPF, Windows UI Automation, xUnit

**Spec:** `docs/superpowers/specs/2026-09-27-prescriva-agent-milestone-1-design.md`

## Global Constraints

- Executar em Windows 10/11 x64 com `net10.0-windows`.
- Nunca expor `AutomationElement` fora de `Prescriva.Agent.Windows`.
- Overlay não pode interceptar mouse nem aparecer como candidato.
- Toda operação UIA aceita cancelamento e possui timeout.
- O Inspector só lê o elemento apontado; não monitora o desktop globalmente.

## Review Focus

- Elemento destruído durante leitura deve virar falha tipada, coberto na Task 3.
- Aplicação x86 inspecionada por Agent x64 não pode travar o dispatcher, coberto no roteiro da Task 5.
- Overlay sob o cursor deve ser ignorado, coberto na Task 4.
- Controle sem `ValuePattern` deve tentar padrões compatíveis, coberto na Task 3.
- Janela fechada durante resolução deve retornar `WindowMissing`, coberto na Task 2.

---

### Task 1: Windows projects and deterministic TestTarget

**Files:**
- Create: `src/Prescriva.Agent.Windows/Prescriva.Agent.Windows.csproj`
- Create: `src/Prescriva.Agent.Desktop/Prescriva.Agent.Desktop.csproj`
- Create: `src/Prescriva.Agent.TestTarget/Prescriva.Agent.TestTarget.csproj`
- Create: `src/Prescriva.Agent.TestTarget/App.xaml`
- Create: `src/Prescriva.Agent.TestTarget/MainWindow.xaml`
- Create: `src/Prescriva.Agent.TestTarget/MainWindow.xaml.cs`
- Create: `tests/Prescriva.Agent.Windows.Tests/Prescriva.Agent.Windows.Tests.csproj`
- Create: `tests/Prescriva.Agent.Windows.IntegrationTests/Prescriva.Agent.Windows.IntegrationTests.csproj`
- Test: `tests/Prescriva.Agent.Windows.IntegrationTests/TestTargetSmokeTests.cs`

**Interfaces:**
- Consumes: solution from foundation plan.
- Produces: TestTarget controls with stable automation IDs and a `--layout-variant` argument.

- [ ] **Step 1: Add a skipped Windows smoke test describing required controls**

Assert the launched TestTarget exposes `MedicationTextBox`, `ConcentrationTextBox`, `QuantityTextBox`, `FormComboBox`, `ItemsGrid`, `NextButton`, `BackButton`, `AddButton`, `FinishButton`, `CancelButton`, `DynamicField` and `ToggleDynamicFieldButton`.

- [ ] **Step 2: Scaffold Windows projects and TestTarget UI**

Target `net10.0-windows`, x64 and WPF where applicable. Add projects to the solution. Implement Add to append a row, Finish to expose a visible completion state and layout variants that move controls without changing semantic IDs.

- [ ] **Step 3: Enable and run the smoke test**

Run: `dotnet test tests/Prescriva.Agent.Windows.IntegrationTests --filter FullyQualifiedName~TestTargetSmokeTests`
Expected: PASS on an interactive Windows desktop.

- [ ] **Step 4: Commit**

Run: `git add src tests Prescriva.Agent.slnx`

Run: `git commit -m "feat: add Windows test target"`

### Task 2: STA UI Automation dispatcher and snapshots

**Files:**
- Create: `src/Prescriva.Agent.Application/Inspection/ElementSnapshot.cs`
- Create: `src/Prescriva.Agent.Application/Inspection/ScreenPoint.cs`
- Create: `src/Prescriva.Agent.Application/Inspection/InspectionResult.cs`
- Create: `src/Prescriva.Agent.Application/Inspection/IElementInspector.cs`
- Create: `src/Prescriva.Agent.Windows/Automation/AutomationDispatcher.cs`
- Create: `src/Prescriva.Agent.Windows/Automation/UiAutomationElementInspector.cs`
- Create: `src/Prescriva.Agent.Windows/Automation/AutomationFailure.cs`
- Test: `tests/Prescriva.Agent.Windows.Tests/Automation/AutomationDispatcherTests.cs`
- Test: `tests/Prescriva.Agent.Windows.IntegrationTests/Automation/ElementInspectionTests.cs`

**Interfaces:**
- Consumes: TestTarget from Task 1.
- Produces: `IElementInspector.FromPointAsync(ScreenPoint, TimeSpan timeout, CancellationToken) -> Task<InspectionResult>` and `FindCandidatesAsync(ApplicationDefinition, TimeSpan, CancellationToken) -> Task<IReadOnlyList<ElementSnapshot>>`.

- [ ] **Step 1: Write failing dispatcher and inspection tests**

Assert all callbacks run on one background STA thread, cancellation ends queued work, timeout returns `TimedOut`, closed window returns `WindowMissing`, and inspection snapshots contain no native UIA reference.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test Prescriva.Agent.slnx --filter "FullyQualifiedName~Automation|FullyQualifiedName~ElementInspection"`
Expected: compile failure for missing interfaces.

- [ ] **Step 3: Implement dispatcher and snapshot adapter**

Own the UIA event loop and element access on the dispatcher thread. Catch element-unavailable and COM failures at the boundary and return stable failure codes.

- [ ] **Step 4: Confirm GREEN and commit**

Run the Task 2 test command; expect PASS. Run `git add src tests`, then `git commit -m "feat: add isolated UI Automation inspection"`.

### Task 3: Selector resolution and UIA capture provider

**Files:**
- Create: `src/Prescriva.Agent.Application/Selection/ISelectorResolver.cs`
- Create: `src/Prescriva.Agent.Application/Selection/SelectorResolution.cs`
- Create: `src/Prescriva.Agent.Application/Selection/ResolvedElementHandle.cs`
- Create: `src/Prescriva.Agent.Application/Capture/ICaptureProvider.cs`
- Create: `src/Prescriva.Agent.Application/Capture/CaptureResult.cs`
- Create: `src/Prescriva.Agent.Windows/Automation/UiAutomationSelectorResolver.cs`
- Create: `src/Prescriva.Agent.Windows/Automation/UiAutomationCaptureProvider.cs`
- Test: `tests/Prescriva.Agent.Windows.IntegrationTests/Automation/SelectorResolutionTests.cs`
- Test: `tests/Prescriva.Agent.Windows.IntegrationTests/Automation/CaptureProviderTests.cs`

**Interfaces:**
- Consumes: snapshots from Task 2 and `SelectorMatcher` from the foundation plan.
- Produces: `ISelectorResolver.ResolveAsync(ElementFingerprint, CancellationToken) -> Task<SelectorResolution>`; `ICaptureProvider.CaptureAsync(ResolvedElementHandle, FieldDefinition, CancellationToken) -> Task<CaptureResult>`.

- [ ] **Step 1: Write failing integration tests**

Cover exact match, moved layout match, deliberately duplicated ambiguous controls, closed window, `ValuePattern`, `TextPattern`, `SelectionPattern`, unsupported pattern and element destroyed during capture.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test tests/Prescriva.Agent.Windows.IntegrationTests --filter "FullyQualifiedName~SelectorResolution|FullyQualifiedName~CaptureProvider"`
Expected: compile failure for missing contracts.

- [ ] **Step 3: Implement resolver and provider**

Keep `ResolvedElementHandle` opaque and valid only inside Windows operations. Return captured text plus provider ID `uia`, confidence, duration and typed failure history.

- [ ] **Step 4: Confirm GREEN and commit**

Run the Task 3 command; expect PASS. Run `git add src tests`, then `git commit -m "feat: resolve and capture UI Automation elements"`.

### Task 4: Click-through overlay and selection controller

**Files:**
- Create: `src/Prescriva.Agent.Application/Inspection/InspectionController.cs`
- Create: `src/Prescriva.Agent.Application/Inspection/InspectionState.cs`
- Create: `src/Prescriva.Agent.Desktop/Overlay/HighlightOverlayWindow.xaml`
- Create: `src/Prescriva.Agent.Desktop/Overlay/HighlightOverlayWindow.xaml.cs`
- Create: `src/Prescriva.Agent.Desktop/Inspection/InspectorViewModel.cs`
- Test: `tests/Prescriva.Agent.Application.Tests/Inspection/InspectionControllerTests.cs`
- Test: `tests/Prescriva.Agent.Windows.IntegrationTests/Overlay/OverlayExclusionTests.cs`

**Interfaces:**
- Consumes: `IElementInspector` from Task 2.
- Produces: `InspectionController.StartAsync`, `ObservePointerAsync`, `ConfirmAsync` and `StopAsync`; observable `InspectionState` with snapshot, bounds, warnings and fingerprint.

- [ ] **Step 1: Write failing controller and overlay exclusion tests**

Assert rapid pointer updates cancel stale inspections, stopping removes highlight, Agent-owned windows are excluded and confirmed selection produces the displayed fingerprint.

- [ ] **Step 2: Confirm RED**

Run: `dotnet test Prescriva.Agent.slnx --filter "FullyQualifiedName~InspectionController|FullyQualifiedName~OverlayExclusion"`
Expected: compile failure for missing controller.

- [ ] **Step 3: Implement controller and overlay**

Set the overlay extended styles to layered, transparent, no-activate and tool-window. Throttle pointer inspection and marshal only immutable state to WPF.

- [ ] **Step 4: Confirm GREEN and commit**

Run the Task 4 command; expect PASS. Run `git add src tests`, then `git commit -m "feat: add visual element inspector"`.

### Task 5: Minimal configurator vertical slice

**Files:**
- Create: `src/Prescriva.Agent.Desktop/App.xaml`
- Create: `src/Prescriva.Agent.Desktop/MainWindow.xaml`
- Create: `src/Prescriva.Agent.Desktop/MainWindow.xaml.cs`
- Create: `src/Prescriva.Agent.Desktop/Configuration/IntegrationEditorViewModel.cs`
- Create: `docs/testing/windows-inspector-manual.md`
- Modify: `docs/handoffs/current-state.md`
- Test: `tests/Prescriva.Agent.Application.Tests/Configuration/IntegrationEditorViewModelTests.cs`

**Interfaces:**
- Consumes: Inspector, configuration store, resolver and capture provider.
- Produces: create integration, select application, add field/trigger, assign semantic ID, save, reload, resolve and read value.

- [ ] **Step 1: Write failing view-model workflow tests**

Use fakes to assert invalid semantic IDs are rejected, unsaved edits are visible, save validates configuration, and reload preserves fingerprints.

- [ ] **Step 2: Confirm RED, implement the minimal WPF flow, then confirm GREEN**

Run: `dotnet test tests/Prescriva.Agent.Application.Tests --filter FullyQualifiedName~IntegrationEditorViewModelTests`
Expected before implementation: FAIL; expected afterward: PASS.

- [ ] **Step 3: Execute documented manual verification**

Run TestTarget and Desktop; select a TextBox, verify click-through highlight, save, restart, resolve and read its value. Repeat against an x86 sample if available and record the result without claiming compatibility when unavailable.

- [ ] **Step 4: Run full verification and commit**

Run: `dotnet build Prescriva.Agent.slnx -c Release` and `dotnet test Prescriva.Agent.slnx -c Release`.
Expected: zero warnings/errors; all non-manual tests pass.

Update handoff, run `git add .`, then run `git commit -m "feat: complete inspector and capture slice"`.

