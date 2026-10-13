# Plan 14 — Win32/Delphi selectors (2026-10-12)

## Why

The first walkthrough on a real pharmacy system (FórmulaCerta 6.0, Delphi VCL; see `docs/testing/milestone-1-manual.md`) found that no field could be resolved:

- **Volatile AutomationId.** VCL gives every child window without a control ID its own window handle as ID (`SetWindowLong(GWL_ID, Handle)`), so UI Automation reports `AutomationId == NativeWindowHandle`. It changes every time the program starts; after a restart the selector no longer matches. Parent panels have the same problem, so the ancestor signal fails too.
- **No labels.** VCL labels (`TLabel`) have no window and do not appear in UI Automation.
- **Too slow.** Under .NET 10, enumerating the Receitas form (117 elements) takes ~8 s and reading their properties ~28 s; the resolver's limit is 10 s. A `CacheRequest` does not help (~15 s). Native `EnumChildWindows` takes milliseconds.

## Design

1. **Volatile AutomationId is not an identity.** An AutomationId equal to the element's own native window handle is treated as absent - for the element and for its ancestors - both when inspecting and when resolving.
2. **Window path.** For such an element the inspector records `ElementFingerprint.WindowPath`: the chain of window classes from the top-level window down to the control, each with its index among same-class siblings (`TPanel[0]/TPageControl[1]/TEdit[3]`). VCL creates child windows in a fixed order, so the path is the same on every start.
3. **Scoring (no new weight).** The window path takes the AutomationId's place as the identity signal and its weight (40): a selector carries one or the other. Existing selectors and approvals are unchanged (weights version stays 2). A Delphi edit's selector (path 40 + control type 15 + class 5 + framework 2 + position 5) is found when its path matches and never guessed without it (27 < 40).
4. **Native resolution.** A selector with a window path is resolved without walking UI Automation: the top-level window's child windows are enumerated natively, each gets a candidate fingerprint (class, path, relative position; the selector's control type when the class matches; the window text as name only for a selector that has a name), the matcher picks one, and only the winner is wrapped with `AutomationElement.FromHandle` for capture and triggers.
5. **Configuration content hash** folds `windowPath` in only when present, so every existing approval stays valid.

## Tasks (each test first)

1. Domain: `ElementFingerprint.WindowPath`; `SelectorMatcher` scores it in the AutomationId slot (`SelectorMatcherTests`).
2. Application: `ElementSnapshot.WindowPath` into the fingerprint (`InspectionController`); content hash unchanged for selectors without a path (`ConfigurationFingerprintTests`).
3. Windows: volatile AutomationId detection, `Win32WindowPath`, inspector snapshot, native resolver path. TestTarget `--layout-variant win32-form` shows a native window whose child controls get their handle as ID, like VCL. Integration test: inspect a field, restart TestTarget (all handles change), resolve and read the same field.
4. Repeat the FórmulaCerta walkthrough on the operator's PC; record results.

## Added while executing

- **Triggers.** On FórmulaCerta the vendor button (`TFagronButton`) reports the Invoke pattern but never raises the Invoke event; a click shows as the button capturing and releasing the mouse (`EVENT_SYSTEM_CAPTURESTART/END`). A window-path trigger listens to those two events of the program's process, for that button only (`Win32ClickWatcher`). Keyboard activation is not detected.
- **Names that are values.** A Delphi date box is named by its value; for a window-path selector the name is recorded only for captioned controls, so the configuration holds no value.

## Result

All tasks done (2026-10-12); results in `docs/handoffs/current-state.md` and `docs/testing/milestone-1-manual.md`.

## Out of scope (follow-ups)

- UI Automation hit-testing from inside the Agent returning its own outline window once the outline covers a large container.
- Reading text of Win32 controls without a Value/Text pattern (`WM_GETTEXT`) - only if the FórmulaCerta walkthrough shows it is needed.
- Window rules that include a program version.
