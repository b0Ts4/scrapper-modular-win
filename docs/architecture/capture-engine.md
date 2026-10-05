# Capture engine

`ICaptureProvider.CaptureAsync(handle, field)` reads one configured field from a resolved element. `UiAutomationCaptureProvider` tries, in order, `ValuePattern`, `TextPattern` and `SelectionPattern` on the dispatcher thread and returns a `CaptureResult` with outcome (`Captured`, `UnsupportedPattern`, `ElementUnavailable`, `TimedOut`), provider ID (`uia`), confidence, duration and the pattern attempts.

Capture happens only when a configured trigger fires and only for the field IDs listed in that trigger's `CaptureFieldsAction` (`SessionCoordinator.HandleTriggerAsync`). A trigger for a different stage captures nothing. Every non-`Found` resolution or non-`Captured` outcome becomes a typed `CaptureFailure` and rejects the whole transition — even for optional fields — so failures stay visible. A `Found` match below 0.8 confidence is used but raises a `SelectorFallback` diagnostic.

Test mode (`IntegrationTestRunner`) uses the same resolver/capture/trigger contracts and keeps the values it reads in memory only (shown as "Valor lido" in the test-mode view).

**Known limits.** `CaptureResult.Confidence` is 1.0/0 today; `FieldDefinition` does not yet customize capture; only UI Automation is implemented (MSAA/Win32/OCR are roadmap items).

## File and image fields

A field configured with `Kind = File` captures a file instead of text (`FileFieldCapture`):

1. If the control exposes text, it must be the fully qualified path of an existing file: the file is copied exactly, read off the UI Automation thread. Above 10 MB → `TooLarge` (not read); missing/unreadable → `FileUnavailable`.
2. If the control exposes no text (an image, a drag-and-drop zone), its own on-screen rectangle is captured as PNG (`source: screen`) — only when it is on screen and topmost at its centre and corners; otherwise `Obscured`, so another window's content is never recorded.

UI Automation does not expose the file behind an image that was dropped onto a control; the on-screen image is the best available copy and is marked as such.

The session coordinator stores the content in `IAttachmentStore` (`SqliteAttachmentStore`: same database, content and original file name encrypted with DPAPI, de-duplicated by SHA-256) **before** the session engine runs, and the field's value becomes `attachment:<SHA-256>`; the payload never carries the content. Any failure rejects the occurrence as `CaptureFailed` (visible, no event). Test mode marks a found-but-unreadable field `FIELD_UNREADABLE` and shows captured files by name, size and origin; the monitor lists attachments by file name. Attachments no longer referenced by any pending or confirmed event are garbage-collected at start-up (after retention); *Clear Local Data* removes them all.
