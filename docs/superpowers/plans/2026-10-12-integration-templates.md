# Plan 15 — Integration templates ("Modelos") with a guided checklist (2026-10-12)

## Why

The operator asked for ready-made integrations ("padrões") that are **not fixed**: a didactic screen where a person adapts them to their own needs and tests them step by step. First need (FórmulaCerta): prescription dosage ("posologia"), the prescriber and their registration (CRM), every component of the formula with its quantity, and the photo of the prescription, saved when *Nova Fórmula* or *Novo Produto* is clicked in the attachment viewer.

## Design

- **Template** (`IntegrationTemplate`, Application): name, explanation, the program it is meant for (process, part of the window title, how to open that screen) and an ordered list of **items** - fields (name, how to find it, kind, required) and buttons (name, how to find it, which fields it reads, event). An item may carry a **suggested position** (a selector, usually a window path) or none ("mark it yourself").
- **Not fixed**: templates are JSON files. Built-in ones ship with the Agent (`Templates\*.json` next to the executable, read-only); a person saves their adjusted version as their own template (`%LOCALAPPDATA%\Prescriva\Agent\templates`). Nothing in code is FórmulaCerta-specific.
- **Guided checklist ("Roteiro")** in step 2, after *Usar este modelo* in step 1:
  - each item shows its status (a fazer / marcado / testado ✓ / falhou ✗ / pulado), what it is and where to find it;
  - per item: *Usar posição sugerida*, *Marcar no programa* (the usual hover + Ctrl/confirm marks **this** item), *Testar este item* (a field is located and read - text, OCR text or image size; a button waits for one click), *Pular*;
  - adjust: rename, change kind or required, remove, add an item; *Salvar como meu modelo*.
  - Marking or using a suggestion adds the field/button to the integration with the item's ID and name; buttons always read every marked field the template lists (or every field). The normal steps 3–5 keep working on the result.
- **Retargeting**: suggested selectors get the chosen program's process and window title when applied, so a different program version (window title "… 6.0.1164") does not break them.
- **Window choice**: *Usar este modelo* uses the program chosen in the list, or finds an open window whose process and title match the template; otherwise it says how to open that screen.

## Built-in templates (FórmulaCerta 6, from the structure seen on 2026-10-12)

1. *FórmulaCerta – Orçamento*: posologia, prescriber council/UF/number/name, components grid (OCR text and image), button *OK*.
2. *FórmulaCerta – Receita*: the same items on the Receitas screen.
3. *FórmulaCerta – Foto da receita (MobyPharma)*: the attachment image (screen image), optional OCR text, buttons *Nova Fórmula* and *Novo Produto*.

The prescriber row's meaning (council, UF, number, name) is inferred from its layout; each item says so and is tested by the person.

## Tasks (test first)

1. Application: template model, validation, retargeting (`TemplateTests`).
2. Infrastructure: `JsonTemplateStore` - built-in + own templates, save own, built-in files valid (`JsonTemplateStoreTests`).
3. Desktop: `TemplateGuideViewModel` - start, suggestion, mark, test field/button, skip, adjust, save as template (`TemplateGuideViewModelTests`, fakes).
4. Desktop UI: template list (step 1) and checklist (step 2).
5. Walkthrough on FórmulaCerta; docs.

## Result (2026-10-12)

Tasks 1–5 done. Added while executing: OCR rows rebuilt from line positions (a grid's cells came back one per line). Not done: an EndToEnd test that drives the checklist UI (exercised by hand on FórmulaCerta). Details in `docs/handoffs/current-state.md`.

## Out of scope

- Reading grid cells one by one (the components grid exposes no cells; OCR text and an image are captured).
- Filling screens in FórmulaCerta (writing) - a separate design.
