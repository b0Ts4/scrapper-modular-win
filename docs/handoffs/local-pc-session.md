# Testing against the real pharmacy system (session on the operator's PC)

A cloud session cannot reach the operator's computer. This work runs in a Claude Code session **on the PC that runs the pharmacy system**: the Code tab of the Claude desktop app, in a clone of this repository. Turn on Computer use only if screen control is wanted.

## Privacy first (non-negotiable)

- The pharmacy system holds patient and prescription data (LGPD).
  - Prefer a test register or a screen without real customers.
  - Screenshots show whatever is on screen.
- `scripts/diagnose-window.ps1` writes **structure only** by default (type, AutomationId, class, position, patterns), never names or values. Use `-IncludeNames` only on a screen without real data.
- Never finish a sale, issue a fiscal document or move money. Mark fields/buttons and run the Agent's **test mode** only; ask the operator to perform any real transaction.
- Same rules as `AGENTS.md`: no keylogger, no clipboard reading, no hidden monitoring; logs never hold captured values.

## Steps

1. `dotnet --version` must start with `10.`. Then:
   ```powershell
   dotnet build Prescriva.Agent.slnx --configuration Release
   ```
2. With the pharmacy system open on the screen to integrate, list the windows and pick its process:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\diagnose-window.ps1
   powershell -ExecutionPolicy Bypass -File scripts\diagnose-window.ps1 -Process <processo>
   ```
   The report (Desktop\diagnostico-janela.txt) says how many text fields and buttons have AutomationIds, and the framework (Win32, WinForm, WPF, Delphi `TEdit`-like classes, Java, Chrome/Electron…).
3. Read the report against the operator's wanted fields and decide, per field, how it can be captured:
   - AutomationId;
   - label (structural selector);
   - Shift (innermost);
   - OCR;
   - screen image.
4. Start the Agent (`src\Prescriva.Agent.Desktop\bin\Release\net10.0-windows10.0.19041.0\Prescriva.Agent.Desktop.exe`) and walk the five steps with the operator, or with Computer use. Run test mode; record what is found or not found and the confidence.
5. Anything that fails gets a RED test first, if it can be reproduced in the TestTarget, then the fix (see `AGENTS.md`). Record the result in `docs/testing/milestone-1-manual.md` and `docs/handoffs/current-state.md`. Do not commit the diagnostic report: it describes the customer's system.
