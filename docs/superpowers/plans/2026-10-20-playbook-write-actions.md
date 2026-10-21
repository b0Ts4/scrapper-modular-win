# Plan 19 — Write actions and playbook engine (assisted launch) (2026-10-20)

Design: [milestone 2](../specs/2026-10-20-milestone-2-automated-quotes-design.md) §4, §7. Approved by the operator on 2026-10-20 ("pode implementar"). Decisions recorded with the approval:

- no robot user: the employee code is a setting (19 in the tests), and everything done in FórmulaCerta is written down in the operator's local test log (`.local/registro-de-testes.md`, not committed);
- the CRM side is built here too (plan 22);
- the CRM keeps its own WhatsApp integration; it is not used for FórmulaCerta (the reply goes through MobyPharma) but is used with other ERPs.

## Goal

The Agent fills a program's screen from data, following a declarative *playbook*, answers known dialogs by rule, stops on anything unknown, and **stops before saving** (assisted level). Proven on the TestTarget and on FórmulaCerta's Orçamentos screen with test data.

## Design

- **Domain `Playbooks`** (no Windows):
  - `Playbook`: id, name, program (process identity + window rule), steps, dialog rules, forbidden buttons, ignored window classes.
  - `PlaybookStep` (kind `setField` | `gridRows` | `click`): a target window path and a value template. `gridRows` names a list in the data and the cell templates.
  - `DialogRule`: class, title start, optional text that must appear (read by OCR), and a response: `press` a button, `fillAndPress` (type into a dialog field, then press), or `stop`.
  - `PlaybookData`: named values and named lists of rows.
  - `PlaybookTemplate`: resolves `{name}`.
  - `PlaybookValidator`: unique ids, required parts per kind, known template syntax, a press button never forbidden, no `click` on a forbidden caption.
- **Application `Playbooks/PlaybookRunner`** over the port `IPlaybookDriver`. For every step it acts, lets the program settle, then handles dialogs:
  - a dialog matching a rule gets its response;
  - a dialog matching no rule, a `stop` rule, a missing button or a forbidden button stops the run.

  After `setField` it reads the value back and stops when it differs; numbers compare as numbers, so "60" = "60,00". A grid row with an empty cell stops before typing, so the program never sees Enter on an empty row. Focus that is blocked by a dialog is answered and then retried once. Cancellation stops the run. The result says completed or stopped, the step, a typed reason and the dialogs answered (rule ids only, never values).
- **Windows `Win32PlaybookDriver`**:
  - finds the program's window (process + window rule, exact title preferred) and controls by window path;
  - gives focus on a worker thread with a timeout;
  - types with `WM_CHAR`, presses keys with `WM_KEYDOWN/UP` and syncs with `SendMessageTimeout(WM_NULL)`;
  - reads with `WM_GETTEXT`;
  - lists the program's other visible top-level windows as dialogs, with their button captions, and reads a dialog's text by OCR of its image;
  - presses buttons with `BM_CLICK`.
- **Infrastructure `JsonPlaybookStore`**: loads and validates playbook files (repeated or unknown properties refused).
- **TestTarget `--layout-variant win32-quote`**: a Delphi-like quote form.
  - Two edits (volume, unit) and a custom grid window.
  - Focusing the grid the first time opens a modal "Funcionário Recepção" dialog with an edit and OK.
  - Enter on the empty row would open a "Salvando" dialog; the test proves it never appears.
  - Product code 999 raises "Atenção!" / "Produto abaixo do Estoque Mínimo!" with an OK button.
  - The grid reports its rows through its window text, for the tests only (a real `TwwDBGrid` does not).
- **Shipped playbook** `formulacerta_lancar_formula.json` (Orçamentos 6.0, assisted): volume and unit, then the components; rules for "Funcionário Recepção" and stock minimum; "Quantidade zerada" stops.

## Tasks (tests first)

1. Domain: playbook model, templates, validator (`PlaybookValidatorTests`, `PlaybookTemplateTests`).
2. Application: `PlaybookRunner` with a fake driver (`PlaybookRunnerTests`):
   - fills in order;
   - read-back mismatch stops;
   - an unknown dialog stops;
   - a rule presses;
   - fill-and-press;
   - blocked focus is retried;
   - an empty cell never sends Enter;
   - a forbidden button stops;
   - cancellation stops.
3. Infrastructure: `JsonPlaybookStore` and the shipped playbook is valid.
4. TestTarget variant + Windows `Win32PlaybookDriver` + integration test `PlaybookRunTests`: the whole quote is filled on the TestTarget, the employee dialog is answered, the stock dialog is answered by OCR rule, and the save dialog never appears.
5. Real run on FórmulaCerta (opt-in test, `PRESCRIVA_FORMULACERTA_PLAYBOOK=1`) with test data: a new quote opened by a person, the playbook fills it and stops before OK, then the quote is cancelled. Written in the local test log.

## Not in this plan

- Saving, and the dialog sequence after OK (plan 20).
- Reading the grid back. The grid shows no text to programs and OCR of it is unreliable (see `docs/integrations/formulacerta-receita.md`). In assisted mode the attendant sees the grid; in plan 20 the saved quote is checked exactly.
- MobyPharma (plan 21), connector and Desktop robot screen (plan 22), replies (plan 23).

## Privacy

Playbooks hold selectors and rules, never business values. Data passes through memory only. Results and logs carry step ids, reason codes and dialog rule ids, never typed values or dialog text.
