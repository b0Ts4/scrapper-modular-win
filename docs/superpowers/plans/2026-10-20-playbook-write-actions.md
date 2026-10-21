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

## Result (2026-10-20)

Tasks 1–5 are done.

**Tests:**
- `PlaybookValidatorTests` (12) and `PlaybookTemplateTests` (8);
- `PlaybookRunnerTests` (21, fake program);
- `JsonPlaybookStoreTests` (6, including the shipped playbook);
- `PlaybookRunTests` (3, live TestTarget `win32-quote`);
- `RealFormulaCertaPlaybookTests`, opt-in.

RED was compile-time for the new API. It was observed at run time for "another screen of the program is not a dialog" (the TestTarget's main window left open: `UnknownDialog HwndWrapper[…]` before the fix).

**Found on the real FórmulaCerta and fixed:**
- *Funcionário Recepção* is `TfrPedeFuncionario`, shown when the focus leaves the components grid. Its OK is a `TFagronButton`, which answers `BM_CLICK`.
- The driver first read values back before the program had handled the posted characters, because a sent `WM_NULL` overtakes posted messages. Characters are now sent synchronously; Tab and Enter stay posted, because Delphi only moves between fields and cells through its own message loop.
- The Receitas screen, open next to Orçamentos, was taken for a dialog. Windows already open when the run starts are now the program's screens; when a modal dialog is already open (Delphi disables every other window), it stays a dialog.
- The OCR of a dialog captured the wrong place on a scaled display: `GetWindowRect` is DPI-virtualized for the Agent. The dialog's bounds now come from UI Automation (physical pixels), as for image fields.
- A click that did not move the focus sent keys to another field (seen by hand). The driver now checks that the focus is in the target before typing.

**From the code review:**
- Typing interrupted by a dialog is never typed again. A retyped grid cell would double a product code: new outcome `Interrupted`, reason `InputInterrupted`.
- A number written with a decimal point ("0.5") stops the run (reason `AmbiguousNumber`): FórmulaCerta reads it the Brazilian way, as 5.

**Real run, 2026-10-20 22:38, test data:** a new quote was opened by hand (Novo Orç. → automático) and the shipped playbook filled it in 17 s:
- volume 60 CAP;
- PREGABALINA 75 mg and CICLOBENZAPRINA (HCL) 5 mg;
- "Produto abaixo do Estoque Mínimo!" recognized by OCR and answered OK;
- "Total Componentes = 2", stopped before OK.

The quote was cancelled. Every run is listed in the operator's local log.

**Limits, kept for later plans:**
- The grid's content is not read back. The program shows no text, and OCR of the grid is unreliable. In assisted mode a person sees it; plan 20 checks the saved quote exactly.
- A focus call held by a dialog that the run then stops on completes when a person answers that dialog, so the focus then moves to the field the robot was going to type in.
- The robot brings the program to the foreground while it types. That is one reason it runs on a dedicated machine.

## Privacy

Playbooks hold selectors and rules, never business values. Data passes through memory only. Results and logs carry step ids, reason codes and dialog rule ids, never typed values or dialog text.
