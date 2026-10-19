# Plan 17 — A question window as trigger, and events that are not repeated (2026-10-18)

## Why

On a test quote (manual walkthrough, "Test quote with two formulas") the Orçamentos *OK* of FórmulaCerta never captured the mouse: it opens a chain of modal questions at once, so the click watcher of plan 14 cannot see it. The right moment is the question **"Mais Fórmula(s)?"** (a `TMessageForm` titled `Orçamento <number>`): the formula is saved, the quote has its number and the formula is still on screen. Its title is shared by the final "Deseja efetuar a inclusão do orçamento?", which would send the last formula twice.

## Design

1. **Trigger event `WindowShown`** (beside `Invoke`): fires when a top-level window of the selector's program appears whose window class equals the selector's `ClassName` and whose title starts with the selector's `Name` (ignoring case). Nothing is resolved beforehand (the window does not exist yet). Implemented with an out-of-context `EVENT_OBJECT_SHOW` hook (`Win32WindowShownWatcher`), on the program's process when known, otherwise on every process filtered by process name. Only the class and title of newly shown top-level windows are looked at: no content, no input.
2. **Events that are not repeated**: `EmitEventAction.UniqueBy` (optional list of field IDs). When the values of those fields equal the ones of the last event of the same type in the session, no event is emitted (the captures still apply). Clearing the session forgets it.
3. **Templates**: a button item may be observed as `windowShown` (`Observe`) and carry `UniqueBy`. The FórmulaCerta *Orçamento* and *Receita completa* templates use the question "Mais Fórmula(s)?" (`TMessageForm`, title `Orçamento `) instead of *OK*, role *send and keep*, unique by `numero_orcamento` + `serie_formula`.

## Tasks (test first)

1. Domain: `UniqueBy` on emit (engine, validator, content hash), session memory; JSON round trip.
2. Windows: `Win32WindowShownWatcher` + `UiAutomationTriggerProvider` for `WindowShown`.
3. Templates: `Observe`, `UniqueBy`; guide builds the trigger and its actions; shipped templates.
4. Walkthrough on FórmulaCerta with a test quote; docs.

## Result (2026-10-18)

Tasks 1–4 done. Tests first: `SessionEngineTests` (unique emit; RED observed), `UniqueEventTests` (content hash RED observed; JSON round trip), `WindowShownTriggerTests` (integration, TestTarget `win32-questions`; RED observed: `NotSupportedException`), `TemplateGuideViewModelTests.A_question_that_appears_...` and `JsonTemplateStoreTests.The_shipped_quote_templates_...` (RED compile-time only: new types). Walkthrough on FórmulaCerta with test quote 899120 (two formulas, simulated data), watched by the Agent's `UiAutomationTriggerProvider` (`WindowShown`, unscoped as for an item of another program), resolver, capture and `SessionEngine` with the shipped *Orçamento* template: three questions seen (two "Mais Fórmula(s)?", one "inclusão"), **two events** - series 0 (45,00 CAP) and series 1 (90,00 CAP), quote number set, grid images ~19 KB, no capture failure, ~0.5–0.9 s to read - and none for the inclusion question. Not yet run through the Agent's window (checklist/test mode/activation) on FórmulaCerta; *Cancelar* of the Orçamentos screen was not checked (it may not capture the mouse either).

## Privacy

The hook sees that *some* window appeared; it reads only the class and title of top-level windows to compare them with the configured rule, keeps nothing and logs nothing.
