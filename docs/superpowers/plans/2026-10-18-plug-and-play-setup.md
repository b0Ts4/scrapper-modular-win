# Plan 18 — Plug-and-play setup from a template (2026-10-18)

## Why

The operator wants the FórmulaCerta integration to be as plug and play as possible. Today, after choosing a template, a person picks the program window, uses the suggested position of each of ~14 items, tests them, runs test mode (which for the "Mais Fórmula(s)?" question means saving a real formula), approves and activates; and the integration is tied to the exact window title, which carries FórmulaCerta's version ("FórmulaCerta - Orçamentos 6.0.1164"), so an update would break it.

## Design

1. **Window rule = exact title or part of it.** A window matches when its title equals the rule or contains it (ignoring case; an exact match is preferred). Existing configurations (exact titles) behave as before. Used by `AutomationWindowLocator`, `WindowsApplicationInstanceSource` and the Desktop's running-window lookup.
2. **Templates aim at the program by its title part.** A template's integration uses its program's `WindowTitleContains` (e.g. "Orçamentos") as window rule; items of another program use that program's own `WindowTitleContains` - no need for that window to be open while setting up.
3. **Automatic verification** (`IntegrationTestRunner.VerifyAsync`): every field must be found and read (as in test mode), every trigger must be **watchable** - its watch established (a button found, a question-window hook started) - instead of having to fire. Outcome `TriggerCheckOutcome.Ready`; a report whose triggers are Ready or Detected and fields Found approves. Manual test mode is unchanged.
4. **"Configurar automaticamente"** (step 1, with a template selected): creates the integration with every suggested field and trigger, saves it, then verifies again every few seconds - telling which screen to open - until everything passes (or the person stops it); then records the approval, activates it and turns on "Iniciar com o Windows". Items without a suggestion are left to do and reported.

## Tasks (test first)

1. Windows: window rule by title part (locator, instance source).
2. Application: `VerifyAsync` with Ready triggers; approval from it.
3. Templates: rule from the template program; `UseAllSuggestions`.
4. Desktop: the one-click setup flow.
5. Walkthrough on FórmulaCerta from scratch: one click, then a real quote; close and reopen Orçamentos; restart the Agent as at Windows start-up.

## Result (2026-10-18)

Tasks 1–5 done. Tests first: `ProcessIdentityDiscoveryTests` (title part, also for a Delphi-like owned form; RED observed), `TemplateGuideViewModelTests` (title part without the window open, `UseAllSuggestions`; RED observed), `IntegrationTestRunnerTests` (`VerifyAsync` Ready/TimedOut; RED compile-time only - new API).

Walkthrough on FórmulaCerta (simulated data, client 98036), as someone installing it: Orçamentos screen closed → Agent → template *Orçamento de fórmula* → **Configurar automaticamente** (the only click). The Agent showed "Aguardando a tela do programa: …"; once the Orçamentos screen was opened it verified and showed "Pronto: … 13 campo(s) e 1 gatilho(s), conferido, aprovado e ativo", with *Iniciar com o Windows* on (Run key `--background`). Then:
- quote 899122 (1 formula): one `orcamento_lido` event; inclusion question "não repetido"; screen closed → session ended;
- Orçamentos reopened → monitoring resumed by itself ("Monitorando gatilho 'mais_formulas'");
- Agent killed and started as at sign-in (`--background`, tray only) → monitoring resumed; quote 899123: `EventPersisted`, then `EventSuppressed` for the inclusion question, `SessionClosed` when the screen closed.

Not covered: the *Receita completa* template end to end (needs an order whose *Nova Fórmula* is enabled); a real Windows restart (simulated by the Run-key command); sending events out of the PC (no backend yet).

## Privacy

Unchanged: only configured elements are read, only when a configured trigger fires; verification reads the fields once to show they work and keeps nothing.
