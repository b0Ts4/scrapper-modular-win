# Windows Inspector and Capture — Task 5 verification (configurator vertical slice)

This documents Task 5's Step 3 ("Execute documented manual verification"). Per the task
brief's own escalation path, a real, automated end-to-end test was attempted first,
before falling back to anything manual. It succeeded for the production pipeline; a
narrower, honestly-scoped manual gap remains for `Prescriva.Agent.Desktop.exe`'s own
compiled WPF UI, documented below rather than claimed.

## What was automated (and passed)

`tests/Prescriva.Agent.Windows.IntegrationTests/ConfiguratorVerticalSliceTests.cs`
(`Full_workflow_create_inspect_confirm_save_reload_resolve_and_read_a_real_value`) drives
the real, production configurator pipeline end to end, with no fakes or mocks anywhere in
the path:

1. Launches a real, compiled `Prescriva.Agent.TestTarget.exe` process (`TestTargetLauncher`,
   the same helper Tasks 2–4's integration tests use).
2. Sets a known value (`"Amoxicillin 500mg"`) on the real `MedicationTextBox` control via
   its real `ValuePattern` (not simulated keystrokes — this just seeds a known value to
   read back later; it is not part of the thing being tested).
3. Drives a real `UiAutomationElementInspector` + `InspectionController` (Task 2/4) with
   the real screen coordinates of `MedicationTextBox`, exactly as `InspectorViewModel`
   would from a live pointer position, until the real element is found.
4. Calls `InspectionController.ConfirmAsync()` to get a real, confirmed
   `ElementFingerprint`.
5. Builds a real `IntegrationEditorViewModel` (this task) wired to a real
   `UiAutomationSelectorResolver`, a real `UiAutomationCaptureProvider`, and a real
   `JsonConfigurationStore` writing to a throwaway temp directory (not a fake store).
6. `CreateIntegration` → `AddStage` → `AddField("medication_name", ..., confirmed
   fingerprint)` → `AddTrigger(...)` → `SaveAsync()`. Asserts `HasUnsavedChanges` goes
   `true` → `false` across the save.
7. Constructs a **second, independent** `IntegrationEditorViewModel` (simulating an app
   restart) pointed at the same directory, and calls `ReloadAsync()`. Asserts the reloaded
   field's selector round-tripped through the real JSON file correctly.
8. Calls `ResolveFieldAsync("medication_name")` against the live desktop — asserts
   `SelectorResolutionStatus.Found`.
9. Calls `ReadFieldValueAsync("medication_name")` — asserts `CaptureOutcome.Captured` and
   that the captured value is exactly `"Amoxicillin 500mg"`, the value seeded in step 2.

Result: **passed**, in a `--configuration Debug` build, on this machine, on
2026-09-29. Command:

```powershell
dotnet test tests/Prescriva.Agent.Windows.IntegrationTests --configuration Debug --filter FullyQualifiedName~ConfiguratorVerticalSliceTests
```

```
Aprovado! – Com falha: 0, Aprovado: 1, Ignorado: 0, Total: 1
```

This proves the full wiring this task set out to build — inspect → confirm → assign a
semantic ID → save → reload → resolve → read — works against a real, live desktop with
real UI Automation, real JSON persistence and no test doubles anywhere in that chain.

### What this automated test deliberately does not cover

It does not drive `Prescriva.Agent.Desktop.exe`'s own compiled `MainWindow` UI (the click
"Start Inspection", move the real mouse, click "Confirm Selection", click "Save", etc.)
through simulated OS-level input (e.g. `SendInput`). Doing so would mean automating a
*second*, independent layer of UI Automation against the Agent's own window — on top of
the UI Automation this test already uses to drive TestTarget — to click WPF buttons at
fixed screen coordinates in this exact window. Per the task brief's own escalation
clause ("if that's genuinely impractical within reasonable effort... fall back to..."),
this was judged not to be worth the added fragility and effort for the marginal proof it
would add beyond driving the real production components directly, which the test above
already does. The gap this leaves is described next.

## Manual verification attempted, and its honest limits

This session runs in a non-interactive shell with `Bash`/`PowerShell` tool access, on a
real, interactive Windows desktop, but **without a way to move the mouse or click**. What
follows is exactly what was and was not verified as a result.

Both real, compiled executables were launched directly (not through a test):

```powershell
./src/Prescriva.Agent.TestTarget/bin/Debug/net10.0-windows/Prescriva.Agent.TestTarget.exe
./src/Prescriva.Agent.Desktop/bin/Debug/net10.0-windows/Prescriva.Agent.Desktop.exe
```

Observed via `Get-Process`, a few seconds after launch:

```
ProcessName                   Id MainWindowTitle              Responding
-----------                   -- ---------------              ----------
Prescriva.Agent.Desktop    17304 Prescriva Agent Configurator       True
Prescriva.Agent.TestTarget  5240 Prescriva Agent Test Target        True
```

**Verified this way:** both processes start, create their main window, and remain
`Responding = True` (not hung, no crash dialog, no unhandled-exception exit) for at least
several seconds with no user interaction. `Prescriva.Agent.Desktop.exe`'s window has the
expected title ("Prescriva Agent Configurator"), confirming `App.xaml`'s `StartupUri` and
`MainWindow`'s `InitializeComponent()` (which constructs `AutomationDispatcher`,
`InspectionController`, `InspectorViewModel`, `IntegrationEditorViewModel`,
`JsonConfigurationStore` and `HighlightOverlayWindow` in its constructor — see
`MainWindow.xaml.cs`) all complete without throwing. Both processes were then stopped
cleanly (`Stop-Process`).

**NOT verified — remains for a human with a mouse and keyboard to check:**

- Clicking "Create Integration" / "Add Stage" and seeing the status text update.
- Clicking "Start Inspection", moving the real mouse over `Prescriva.Agent.TestTarget`'s
  `MedicationTextBox`, and visually confirming the blue click-through highlight rectangle
  (`HighlightOverlayWindow`) appears over the correct control, tracks the pointer, and
  never itself intercepts a click (its click-through / `WS_EX_TRANSPARENT` behavior *is*
  covered by a real, automated test — `OverlayExclusionTests`, Task 4 — but that test
  asserts on `InspectionController`'s output, not on what a human sees on screen).
- Clicking "Confirm Selection", typing a semantic ID in the "4. Assign semantic ID" panel,
  clicking "Add Field From Confirmed Selection", clicking "Save", closing and relaunching
  `Prescriva.Agent.Desktop.exe`, clicking "Reload From Disk", and clicking "Resolve" /
  "Read Value" — the exact sequence `ConfiguratorVerticalSliceTests` already exercises
  through the underlying components, but not through this window's actual buttons and
  text boxes.
- General UI polish: layout at different window sizes, tab order, whether the status
  messages are clear to a first-time user.

No claim is made that this manual click-through was performed — it was not. The
automated test above is the real evidence for the underlying workflow; this section
records only what launching the real executables (without interacting with them) could
and could not show.

## x86 sample application

The brief asks to repeat verification against an x86 sample application "if available".
There is no x86 sample application anywhere in this repository — `Prescriva.Agent.TestTarget`
is the only sample/target application that exists, and it is built `PlatformTarget=x64`
(as is every other project in this solution; see each `.csproj`). No x86 verification was
performed, and no claim of x86 compatibility is made. Building and verifying an x86
sample application was out of scope for this task (the brief only asks to repeat the
existing verification against one "if available", not to build one), and would be a
reasonable follow-up for a future task if x86 support is ever required.

## Environment notes affecting this verification

Both the automated test above and the manual executable launches were affected by the
same, already-documented Windows Smart App Control issue (see `docs/handoffs/current-state.md`
and `docs/testing.md`): a `FileLoadException` (`0x800711C7`) can block a freshly built,
unsigned assembly from loading, unpredictably, in either Debug or Release, depending on
this machine's per-assembly reputation state. During this task's verification:

- `Prescriva.Agent.TestTarget.exe` and `Prescriva.Agent.Desktop.exe` (Debug) launched
  successfully for the manual check above.
- `ConfiguratorVerticalSliceTests` passed in `--configuration Debug`.
- The same test, and most of `Prescriva.Agent.Windows.IntegrationTests`, failed in
  `--configuration Release` on this machine at the time of this run, purely because
  `Prescriva.Agent.TestTarget.exe` (Release) was blocked from loading its own DLL — not
  a test or product defect. `Prescriva.Agent.Domain.Tests.dll` and
  `Prescriva.Agent.Infrastructure.Tests.dll` (Debug) hit the identical block on a
  different run a few minutes later, while `Prescriva.Agent.Windows.IntegrationTests`
  (Debug) passed 21/21 in that same run. Which specific assembly gets blocked shifts
  between runs on this machine, consistent with the reputation-based (not deterministic)
  nature of the block already documented elsewhere in this repo.
- Every test suite in the solution passed in full at least once across the Debug/Release
  runs performed for this task; no failure traced to anything other than this
  already-known environmental block. See `docs/handoffs/current-state.md` for the
  consolidated, current test counts.
