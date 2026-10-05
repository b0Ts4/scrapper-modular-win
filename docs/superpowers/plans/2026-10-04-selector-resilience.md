# Prescriva Agent Selector Resilience Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reencontrar controles que não têm `AutomationId` estável (o caso comum em ERPs), usando os sinais estruturais da spec §6 — cadeia de ancestrais, rótulo próximo e posição relativa — sem nunca escolher silenciosamente um candidato ambíguo; e tornar visível quando uma integração está `Degradada` ou `Quebrada` (spec §8).

**Architecture:** Domain continua dono da pontuação (`SelectorMatcher` + `SelectorWeights` versionados). Windows passa a extrair os mesmos sinais estruturais tanto ao inspecionar (fingerprint salvo) quanto ao resolver (candidatos), num único helper para que os dois lados nunca divirjam. Application acompanha a saúde da integração a partir dos diagnósticos do runtime; Desktop só exibe.

**Tech Stack:** C# 14, .NET 10, UI Automation, WPF, xUnit

**Spec:** §6 Selector Engine, §8 (Degraded/Broken), ADR-003.

## Global Constraints

- Ambiguidade continua explícita: pontuação mínima **e** margem entre os dois melhores.
- Posição pesa menos que identificadores semânticos e estruturais.
- Pesos e limiares versionados (`SelectorWeights.Version` passa a 2) e cobertos por testes.
- Nenhum valor capturado entra em fingerprint, log ou diagnóstico (rótulos são texto estático da interface, não valores de campo).

## Review Focus

- Um campo sem `AutomationId`, identificado pelo rótulo, deve ser encontrado mesmo após mudança de layout — Tasks 1–3.
- Dois campos sem `AutomationId` e com o mesmo rótulo devem ser `Ambiguous` — Task 1.
- Uma correspondência completa dos sinais que o seletor possui deve ter confiança 1,0 (hoje `AutomationId`+`ControlType` vale 0,65 e dispara "fallback" à toa) — Task 1.
- O valor digitado num campo nunca pode virar "rótulo" de outro — Task 2.

---

### Task 1: Weights v2 and confidence relative to the selector's own signals

**Files:**
- Modify: `src/Prescriva.Agent.Domain/Selectors/SelectorWeights.cs`, `SelectorMatcher.cs`, `SelectorMatch.cs`
- Test: `tests/Prescriva.Agent.Domain.Tests/Selectors/SelectorMatcherTests.cs`

- [ ] **Step 1: Failing tests** — a control with no AutomationId is `Found` by label + ancestors + type; the same control after a position change is still `Found`; two candidates sharing label/type/ancestors are `Ambiguous`; position alone never beats a semantic identifier; full match of the signals a selector has gives confidence 1.0; a partial match gives score / available weight; version is 2.
- [ ] **Step 2: RED** — `dotnet test tests/Prescriva.Agent.Domain.Tests --filter FullyQualifiedName~SelectorMatcher`.
- [ ] **Step 3: Implement** — weights: AutomationId 35, ControlType 15, Name 12, ClassName 5, FrameworkId 3, NearbyLabels 15, Ancestors 10, RelativeBounds 5 (= 100); MinimumScore 45, MinimumLead 10. Confidence = score ÷ sum of weights for signals present in the selector.
- [ ] **Step 4: GREEN, commit** — `feat: weights v2 and confidence relative to available signals`.

### Task 2: Structural signals from UI Automation

**Files:**
- Create: `src/Prescriva.Agent.Windows/Automation/StructuralSignals.cs`
- Modify: `UiAutomationElementInspector.cs`, `UiAutomationSelectorResolver.cs`, `src/Prescriva.Agent.Application/Inspection/ElementSnapshot.cs`, `InspectionController.cs`
- Modify: `src/Prescriva.Agent.TestTarget/MainWindow.xaml(.cs)` — add labelled fields without AutomationId (`Observações`, `Lote`), moved by `--layout-variant alternate`, plus a duplicate-label variant.
- Test: `tests/Prescriva.Agent.Windows.IntegrationTests/Automation/StructuralSelectorTests.cs`

- [ ] **Step 1: Failing tests** — the inspected fingerprint of `Observações` has no AutomationId but has its label, ancestors and relative bounds; it resolves `Found` in default and alternate layouts; duplicate-label variant resolves `Ambiguous`; a text box's own value never appears as a label.
- [ ] **Step 2–4: RED, implement (`LabeledBy`, else nearest preceding `Text` sibling; up to 3 ancestors; bounds relative to the window), GREEN, commit** — `feat: capture and match structural selector signals`.

### Task 3: Integration health (Degraded / Broken)

**Files:**
- Create: `src/Prescriva.Agent.Application/Runtime/IntegrationHealth.cs`
- Modify: `src/Prescriva.Agent.Desktop/Monitoring/RuntimeMonitorViewModel.cs`, `MainWindow.xaml(.cs)`
- Test: `tests/Prescriva.Agent.Application.Tests/Runtime/IntegrationHealthTests.cs`, `RuntimeMonitorViewModelTests.cs`

- [ ] Healthy by default; `SelectorFallback` → Degraded (reason names the field); `TriggerWatchFailed` or a rejection caused by a capture failure → Broken (reason names trigger/field); a recovered watch and later clean captures return to Healthy; a missing required *value* (operator left it empty) is not a configuration problem and keeps Healthy. Shown in the monitor with the reason. Commit `feat: show integration health with its reason`.

### Task 4: End-to-end verification and handoff

- [ ] Desktop walkthrough: select `Observações` (no AutomationId) with the real cursor as an optional captured field, test, approve, activate, Add → event contains it; restart TestTarget with the alternate layout → still captured. Docs (selector-engine, testing, roadmap, handoff). Commit `docs: record selector resilience verification`.
