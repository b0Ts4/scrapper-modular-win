# Milestone 2 — Automated attendance and quotes in FórmulaCerta (design, 2026-10-20)

Status: draft for the operator's approval. Nothing here is implemented yet.

## 1. Goal

The Agent attends online orders in FórmulaCerta **the way an attendant does today**, driven by the CRM (`b0Ts4/receitaAI`, "Receita Fácil"):

1. a new order arrives in MobyPharma (from WhatsApp / online store) with a prescription attached;
2. the Agent takes the order ("Atender"), copies the prescription and sends it to the CRM;
3. the CRM transcribes it (receitaAI API) and a person (attendant/pharmacist) reviews and confirms the formula there;
4. the CRM sends the Agent a job "launch this formula for this order";
5. the Agent opens *Nova Fórmula* from the order, fills the Orçamentos screen, saves the quote and reads back its number and value;
6. the CRM composes the answer to the customer; once approved, the Agent writes it in the order's chat in MobyPharma (the same WhatsApp channel the pharmacy already uses).

FórmulaCerta keeps every business rule: prices, excipient, packaging, stock, numbering. The Agent only types what a person would type and reads what a person would read.

There is no public Fagron API, so the integration is UI automation on a **dedicated Windows machine (or VM)** with its own FórmulaCerta user.

## 2. Evidence (proof of concept, 2026-10-20)

On the real FórmulaCerta 6.0.1164, test data only, nothing saved:

- A program filled a whole capsule formula without mouse or physical keyboard:
  - volume 60 CAP;
  - Pregabalina 75 mg (code 25531) + Ciclobenzaprina 5 mg (code 7995).

  Each control was found by the same window paths the shipped templates use (e.g. `TFagronPageControl[0]/TcxTabSheet[0]/TwwDBEdit[7]`, grid `TFagronPanel[2]/TwwDBGrid[0]`). Text was sent as `WM_CHAR` and keys as `WM_KEYDOWN/UP` after `SetFocus`. Dialog buttons were pressed with `BM_CLICK`. FórmulaCerta found the products, computed values and showed "Total Componentes = 2"; the run stopped before OK.
- Learned behaviour that the design must cover:
  - **Field order matters.** Fill form, volume and unit before the components; otherwise "Quantidade zerada na ficha de pesagem" appears.
  - **"Funcionário Recepção"** (employee code) is asked the first time focus enters the formula. It is shown from inside the focus change, so a blocking `SetFocus` hangs: focus must be given without waiting forever.
  - **Informational dialogs** appear while typing, e.g. "Produto abaixo do Estoque Mínimo!" (`TMessageForm` "Atenção!", one OK button).
  - **Dialog text cannot be read by program.** Delphi draws labels as graphics. Titles and button captions can be read, so the dialog text must be read by OCR, which the Agent already has.
  - **Enter on the empty grid row starts saving** (employee → Q.S.P.? → …). This is never allowed.
  - **Two `TwwDBGrid` controls exist.** Address the components grid by path, never "the first grid".
  - Tooltips (`THintWindow`) are not dialogs.
- Already known from milestone 1 work:
  - the dialogs after OK (Q.S.P.?, Dados Corretos?, Embalagem, Condições de Pagamento, Acréscimo/Desconto, Retirada, Retirada de Fórmulas, "Mais Fórmula(s)?");
  - *Nova Fórmula* from an order closes the screen after the last "Não";
  - *Nova Fórmula* is enabled only for an order taken by the signed-in user;
  - the Agent already copies the attachment file and reads the quote number and series.

## 3. Principles and hard rules

- **Declarative, not ERP code.** What to type, in which order, and how to answer each dialog lives in versioned JSON *playbooks* (like templates). The engine is generic.
- **Visible operation.** While the robot works, the Agent shows a banner "Robô trabalhando — pedido N" with a **Parar** button (and a hotkey). Stopping leaves the screen as it is.
- **Stop on anything unknown.** The Agent stops, leaves the screen untouched, and reports the step with a screenshot of the program window only. Unknown means:
  - an unrecognised dialog;
  - a product not found;
  - a value that does not read back as typed;
  - a control not found;
  - a timeout.
- **Verify after typing.** Every field and grid cell is read back. Every grid line must show the expected product code, quantity and unit before going on.
- **Never:**
  - press Enter on an empty grid row;
  - print;
  - create a requisition;
  - finish a sale;
  - approve a payment;
  - change a price;
  - delete anything;
  - change a customer's registration.

  Some of these are dialogs that may appear (print, requisition); the playbook may only cancel or decline them.
- **One job at a time, idempotent.** A job carries `(orderId, series)` (or a job id). A job already done returns the stored result instead of launching again.
- **People decide.** The formula is confirmed by a person in the CRM before the Agent launches it. The reply to the customer is approved before it is sent, at least until the operator turns on automatic sending for simple cases.
- **Privacy (unchanged rules).**
  - Job payloads and results hold patient data: they are stored like events (local SQLite, DPAPI) and never written to technical logs.
  - Screenshots for failures are of the program window only and are stored the same way.

## 4. Architecture

```
            CRM (receitaAI)  ◄──── HTTPS, Agent connects out (no open port) ────►  Agent (dedicated PC)
  transcription · review · reply text                                   ┌──────────────────────────────┐
                                                                        │ Connector  (pull jobs,        │
                                                                        │            push events/results)│
                                                                        │ Job store  (SQLite + DPAPI)    │
                                                                        │ Job runner (playbook engine)   │
                                                                        │ Write provider (Win32)         │
                                                                        │ Dialog watcher + OCR           │
                                                                        └──────────────┬───────────────┘
                                                                                       ▼
                                                                    FórmulaCerta: MobyPharma · Orçamentos
```

### Domain (no Windows)
- **Jobs.** `Job` (id, kind, order reference, payload, status), `JobResult`, `JobFailure` (step, reason code, message without values). The kinds are:
  - `AttendOrder` — take the order, copy the attachments, report them;
  - `LaunchQuote` — one or more formulas for an order;
  - `SendReply` — write a message in the order's chat.
- **Formula payload.**
  - Fields: form code, volume + unit, pots, dosage (posologia), prescriber (council, UF, number), observations.
  - Components: product code, quantity, unit, and the expected description used for verification.
- **Playbook.** An ordered list of steps:
  - `SetField(selector, value)`;
  - `GridRow(gridSelector, cells)`;
  - `Click(selector)`;
  - `WaitWindow(rule)`;
  - `AnswerDialog(rule → button | stop)`;
  - `ReadBack(selector → result field)`;
  - `Repeat(for each component / formula)`.

  Values come from the job (`{formula.volume}`) or the Agent's settings (`{settings.employeeCode}`).
- **Dialog rules.** A rule matches on window class + title prefix + an OCR text pattern, and gives the action: press a named button, type a value then press a button, or stop. Unmatched dialogs always stop.
- A validator for playbooks, like the configuration validator: known selectors, a `stop` default, no `Enter` on an empty row, no forbidden buttons (print, requisition, finish, approve).

### Application
- `JobRunner`: a state machine that executes a playbook against `IWriteProvider` and `IDialogWatcher`. It has timeouts per step, verifies every write by reading back, and records progress per step so a stopped job can be reported precisely. It does not resume blindly: a stopped job needs a person to look.
- Ports:
  - `IJobInbox` — pull or receive jobs;
  - `IResultOutbox` — results, failures, order events;
  - `IJobStore`.
- Reuses capture (fields, attachment file), triggers (`WindowShown`) and OCR from milestone 1.

### Infrastructure
- SQLite job store (DPAPI), shared retention rules with events.
- Connectors:
  1. **Local connector** (first): a localhost-only HTTP endpoint with a token, so the CRM or a test script on the same network can post jobs and read results. Used for tests and the first pilot.
  2. **CRM cloud connector** (when the CRM side exists): the Agent polls `GET /agent/jobs` and posts `POST /agent/results` and `POST /agent/events` over HTTPS with a per-machine token. It works behind NAT with no port opening. This also delivers the milestone-1 events (the roadmap's "Transport" item).

### Windows
- `Win32WriteProvider`:
  - focus is given with `AttachThreadInput` + `SetFocus` on a worker thread with a timeout, because focus can open a modal dialog;
  - text is sent as `WM_CHAR` and keys as `WM_KEYDOWN/UP`;
  - buttons are pressed with `BM_CLICK`;
  - values are read back with `WM_GETTEXT`, plus grid image/OCR for cells.
- `DialogWatcher`: the existing `WindowShown` hook for the program's process, plus OCR of the dialog image for its text. `THintWindow` and `TApplication` are ignored.

### Desktop
- A **Robô** screen:
  - on/off;
  - the queue (job, order, status, step);
  - the last failures with their screenshot;
  - the **Parar** button;
  - settings: employee code, which playbooks are enabled, autonomy level.
- The banner while a job runs.

### Playbooks shipped (FórmulaCerta 6.0)
- `formulacerta_atender_pedido`: select the order in MobyPharma → Atender (answer "Sim") → copy each attachment (existing file capture) → event `order_taken`.
- `formulacerta_lancar_orcamento`: from the order → *Nova Fórmula* → employee → for each formula:
  - form, volume and unit;
  - pots;
  - dosage;
  - prescriber;
  - components, each line verified;
  - OK;
  - the dialog sequence. Q.S.P. follows the job; "Dados Corretos? Sim"; packaging and payment keep the defaults; "Mais Fórmula(s)?" answers Sim or Não by the formulas left.
  - read back quote number, series, value.
- `formulacerta_responder_pedido`: the order's message box → type the approved text → *Salvar/Responder*. The chat screen is still to be mapped.

## 5. CRM contract v0 (JSON over HTTPS)

Agent → CRM events:
- `order_taken` {orderId, branch, customerName, receivedAt, attachments[{name, mime, bytes(base64), sha256}]}
- `quote_launched` {jobId, orderId, quotes[{number, series, total, components[{code, description, quantity, unit}]}]}
- `job_failed` {jobId, step, reason, screenshot?}
- `reply_sent` {jobId, orderId, sentAt}

CRM → Agent jobs:
- `attend_order` {jobId, orderId}
- `launch_quote` {jobId, orderId, formulas[{form, volume, unit, pots, dosage, prescriber{council, uf, number}, components[{code, quantity, unit, expectedDescription}]}]}
- `send_reply` {jobId, orderId, text}

Product mapping is a CRM responsibility. The CRM sends FórmulaCerta product codes; receitaAI's `product_catalog_v1.csv` already holds them. The Agent verifies the description FórmulaCerta shows against `expectedDescription` and stops on a mismatch.

## 6. Autonomy levels (setting, per playbook)

1. **Assistido:** the Agent fills the screen and stops before OK. The attendant reviews and saves. This is the first pilot.
2. **Automático com conferência:** the Agent saves; the result goes back to the CRM for a person to approve the reply.
3. **Automático:** saves and replies without approval, only for formulas the operator marks as simple. This is a later decision, not part of this milestone's default.

## 7. Plans (each one small, tested and reviewed)

| Plan | Delivers | Proven on |
|---|---|---|
| 19 Write actions + playbook engine | Domain playbook/dialog rules + validator; `JobRunner`; `Win32WriteProvider`; launch a formula and **stop before OK** (assisted) | TestTarget form with a Delphi-like grid; FórmulaCerta with test data |
| 20 Save and read back | The after-OK dialog sequence by rules; read number/series/value; stop on unknown dialogs | FórmulaCerta, test client, then cancel/inform the technician |
| 21 Attend an order | MobyPharma: find the order, Atender, attachments, *Nova Fórmula* into plan 19/20 | a test order (operator creates one) |
| 22 Connector | Job store, local connector, contract v0; CRM cloud connector when the CRM endpoint exists | test script posting jobs; then the CRM |
| 23 Reply in the chat | Map the MobyPharma chat; `send_reply` with approval | a test order |

## 8. Open questions for the operator

1. A **FórmulaCerta user and employee code for the robot** (and a licence), so that its quotes are identifiable.
2. The **dedicated machine/VM** and who signs in to it. The robot needs an unlocked Windows session.
3. Which dialogs a pharmacist accepts as **informational** (stock minimum, …) and which must stop the robot. Controlled substances (SNGPC) stop by default.
4. Whether a **test order** can be created in MobyPharma for plans 21 and 23.
5. Who builds the CRM side (endpoints of §5) and where it is hosted.
