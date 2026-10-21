# Plan 22 — Robot jobs, local API and CRM connector (2026-10-21)

Design: [milestone 2](../specs/2026-10-20-milestone-2-automated-quotes-design.md) §4, §5. The operator asked for everything to be implemented and tested.

## Goal

The CRM sends the Agent a job "launch this quote" and gets the result back without anyone at the Agent's screen:

1. a job arrives, either posted to the Agent's local API (the pharmacy's network, token) or pulled from the CRM (HTTPS, the Agent connects out);
2. it waits in an encrypted local queue;
3. the robot runs it: one playbook run per formula, with the pharmacy's notice choices, remembering any notice it stops on;
4. the result goes back: quote number and values read from the screen, or where and why the robot stopped.

## Design

- **Contract v0** (JSON, camelCase): `launch_quote` `{jobId, orderId?, formulas[{volume, volumeUnit, qsp?, dosage?, components[{code, quantity, unit, expectedDescription?}]}]}`. Numbers use a decimal comma; a decimal point is refused by the runner.
  - The result is `{jobId, state, formulas[{captured values}], stop?{step, reason, detail}, unknownNotice?{title, buttons, text}}`.
- **Application `Jobs`:**
  - `RobotJob`: id, kind, state, request JSON, result JSON, times, a summary without values.
  - `IJobStore`: adding is idempotent by job id, so the same job sent twice runs once.
  - `QuoteRuns`: turns a request into playbook data per formula. It sets "more formulas" yes or no; assisted level means `salvar` no and only the first formula.
  - `JobProcessor`: runs the next queued job and stores its result.
- **Infrastructure:**
  - `SqliteJobStore`: `robot.db`, request and result encrypted with DPAPI like events.
  - `LocalJobApi` (`HttpListener` on 127.0.0.1, bearer token):
    - `POST /v1/jobs/launch-quote`;
    - `GET /v1/jobs/{id}`;
    - `GET /v1/jobs`.
  - `CrmJobConnector`:
    - pulls `GET {crm}/v1/agent/jobs/next`;
    - posts `POST {crm}/v1/agent/jobs/{id}/result` for every finished job not yet reported.
- **Desktop, step 6:**
  - robot on/off, level (assisted or saves), employee code, CRM address and token (DPAPI), the local API token;
  - the job list and **Parar**, which cancels the running job and leaves the screen as it is.
- **CRM side (receitaAI):** an `agent_bridge` router.
  - The CRM creates jobs; the Agent takes the next one; the Agent posts results; the CRM reads a job's state.
  - It uses its own agent token and SQLite queue.
  - It is written in the receitaAI repository, on its own branch.

## Tasks (tests first)

1. `QuoteRuns`, `JobProcessor` (fake program), `SqliteJobStore`.
2. `LocalJobApi`, `CrmJobConnector` (a fake CRM over HTTP).
3. End to end on the TestTarget quote screen: a job posted to the local API is filled by the robot and its result is read back through the API.
4. Desktop step 6. CRM side in receitaAI.

## Result (2026-10-21)

Tasks 1–4 are done.

**Tests:**
- `QuoteRunsTests` (7), `JobProcessorTests` (7), `RobotLoopTests` (4);
- `SqliteJobStoreTests` (5), `JobConnectionTests` (6), `JsonRobotConfigurationStoreTests` (2);
- `RobotJobEndToEndTests` (2) and `PlaybookRunTests` (+1: a notice already open when the robot starts).

**By hand, in the real Agent** (isolated data directory, TestTarget quote screen, test playbook in the data directory):
1. A job posted to `http://127.0.0.1:18800/v1/jobs/launch-quote` was run by the robot while the banner "Robô trabalhando … [Parar]" was shown.
2. The grid ended as `emp=19;rows=25531|75|MG;999|5|MG`, with the employee answered and the stock notice recognized by OCR and answered.
3. `GET /v1/jobs/teste-agent-2` returned `completed`.
4. After a restart the robot resumed by itself and the job list was kept.

**Found by hand and fixed:**
- The first try stopped on the TestTarget's own main window. "Funcionário Recepção" had opened before the robot started, when the Agent's window took the focus, and the TestTarget disabled only the quote form behind a modal.
- It now disables every window of the program, as Delphi's `ShowModal` does. New test: `A_notice_already_open_when_the_robot_starts_…`.

**CRM side:** branch `feature/agent-bridge` of `b0Ts4/receitaAI`, with `agent_bridge.py`, `test_agent_bridge.py` and `AGENT_BRIDGE.md`. **Its tests have not been run:** this machine has no Python.

## Privacy

- Job requests and results hold patient and prescription data: encrypted at rest, never in technical logs (only job id, state and reason codes).
- The local API listens on 127.0.0.1 only and needs its token.
