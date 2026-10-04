# Capture engine

`ICaptureProvider.CaptureAsync(handle, field)` reads one configured field from a resolved element. `UiAutomationCaptureProvider` tries, in order, `ValuePattern`, `TextPattern` and `SelectionPattern` on the dispatcher thread and returns a `CaptureResult` with outcome (`Captured`, `UnsupportedPattern`, `ElementUnavailable`, `TimedOut`), provider ID (`uia`), confidence, duration and the pattern attempts.

Capture happens only when a configured trigger fires and only for the field IDs listed in that trigger's `CaptureFieldsAction` (`SessionCoordinator.HandleTriggerAsync`). A trigger for a different stage captures nothing. Every non-`Found` resolution or non-`Captured` outcome becomes a typed `CaptureFailure` and rejects the whole transition — even for optional fields — so failures stay visible. A `Found` match below 0.8 confidence is used but raises a `SelectorFallback` diagnostic.

Test mode (`IntegrationTestRunner`) uses the same resolver/capture/trigger contracts and keeps the values it reads in memory only (shown as "Valor lido" in the test-mode view).

**Known limits.** `CaptureResult.Confidence` is 1.0/0 today; `FieldDefinition` does not yet customize capture; only UI Automation is implemented (MSAA/Win32/OCR are roadmap items).
