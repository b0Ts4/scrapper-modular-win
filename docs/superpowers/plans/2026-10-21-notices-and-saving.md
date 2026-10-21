# Plan 20 — Notices configurable per pharmacy, saving and reading the quote back (2026-10-21)

Design: [milestone 2](../specs/2026-10-20-milestone-2-automated-quotes-design.md) §3, §4, §7.

The operator said "a questão dos avisos varia para cada farmácia, então o ideal é que isso seja configurável. Pode seguir, implemente tudo e teste".

## Goal

1. **Notices per pharmacy.** A playbook ships default dialog rules. Each pharmacy keeps its own choices next to them, in its local data, and changes them in the Agent without editing JSON:
   - for a known notice: stop, or press one of its buttons;
   - for notices the robot met and stopped on: the robot remembers them (class, title, buttons, text read from the screen), so the pharmacy can decide what to do next time.
2. **Saving and reading back.** The FórmulaCerta playbook presses OK and answers the dialogs after it by rule. The answers depend on the data: Q.S.P. yes or no, more formulas yes or no. The playbook reads back the quote number and anything else the dialogs show (series, value).

## Design

- **Domain.**
  - `DialogPolicy` (playbook id + rules): a pharmacy's rules. A rule with the id of a playbook rule replaces it; new rules come before the playbook's. `Playbook.WithPolicy` merges the two; the merged playbook is validated like any other, so a forbidden button cannot be configured.
  - `DialogRule` gains:
    - `button` may be a template (`{qsp}`);
    - `captureTitleAs`: the title after the matched start becomes a value (e.g. the quote number in "Orçamento 899124");
    - `captureField` + `captureAs`: a dialog field read before answering.
  - New step `readField` (target, `captureAs`).
- **Application.**
  - `PlaybookRunner`:
    - waits for the program's window (`AttachTimeout`), because *Nova Fórmula* opens Orçamentos a moment later;
    - returns `Captured` (in memory only, never logged);
    - on an unknown dialog, returns `Unknown` (class, title, buttons, text) so the Agent can remember it.
  - `IDialogPolicyStore`: the pharmacy's policy, plus the notices seen, per playbook.
  - `NoticeSettingsViewModel` (Desktop, tested like the other view models): one row per notice (playbook rule, pharmacy rule or seen notice), each with the choices "Parar e chamar alguém" or "Apertar «botão»". Saving writes the policy.
- **Infrastructure.** `JsonDialogPolicyStore` under the Agent data directory (`playbooks/<id>.avisos.json`). Repeated or unknown properties are refused.
- **Desktop.** An "Avisos do robô" window opened from the main window.
- **Playbook.** `formulacerta_lancar_formula` gains OK and the rules for the dialogs after it. They are found on the real program by running and stopping on each unknown one, which is the feature itself.

## Tasks (tests first)

1. Domain: policy merge, new rule and step fields, validator.
2. Application: runner waits for the window, captures values, reports unknown dialogs.
3. Infrastructure: policy store. Desktop: view model and window.
4. Real FórmulaCerta, test data: save a quote through the dialogs and read its number. Each saved quote goes to the operator's local log.

## Result (2026-10-21, partial)

Tasks 1–3 are done.

**Tests:**
- `DialogPolicyTests` (6);
- `PlaybookRunnerTests` (+9: waiting for the window, unknown notice reported, title and field captures, data button and forbidden data button, `onlyWhen`);
- `JsonDialogPolicyStoreTests` (7);
- `NoticeSettingsViewModelTests` (6).

The step 6 "Robô" tab shows each notice with its choices. It was checked by hand in the Agent: a notice the robot met is listed as "Visto 1 vez" with "Parar e chamar uma pessoa".

**Task 4 (saving on the real FórmulaCerta) is not done.** Running the playbook with `salvar` would create a real quote in the pharmacy's production system. The session's safety check refused it, and even refused confirming "Dados não gravados → Sim" on an empty unsaved quote. It needs the operator's explicit authorization, or a FórmulaCerta test database.

The playbook already has OK (`TFagronButton[8]`, only when `salvar`). The rules for the dialogs after OK are to be found by that run: each unknown one stops it and is recorded for the pharmacy.

## Privacy

- Captured values are business data. They stay in memory and go to the job result only, never to technical logs.
- The text of a seen notice is kept in the local policy file so a person can decide on it. A notice that shows patient data would be kept too, so the file lives in the Agent's local data directory, never in Git.
