# Prescriva Agent Screen-Image Fields Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** capture an image that the program only *shows* on screen (not a file, not an input).

**Request (2026-10-09):** "no campo de captura de imagem ... ele só exibe uma imagem na tela, aí eu preciso capturar a imagem. Então não é um input, é basicamente uma imagem."

## Design

- **Domain** — `FieldKind.ScreenImage = 3` (JSON `"screenImage"`). `FieldKind.ProducesAttachment()` is true for `File` and `ScreenImage`.
- **Windows** — a screen-image field always takes the control's own on-screen rectangle as PNG.
  - It never reads the control's text and never opens a file it names.
  - It is refused (`Obscured`) when anything covers the control, so no other window is ever recorded.
- **Application** — the runtime stores the image as an attachment, exactly like a file field. A capture without an image is unreadable in test mode and rejects the occurrence at run time.
- **Desktop** — the field type *Imagem exibida na tela (captura da imagem)* in step 2; the summary shows `[image]` and the field list "imagem da tela".
- The existing *Arquivo / imagem* type is unchanged: file path → copy of the file, no text → screen image.

### Tasks
- [x] RED/GREEN:
  - Domain/Infrastructure `FieldKindTests` (round trip);
  - `SessionCoordinatorTests` (attachment stored; no image rejects);
  - `IntegrationTestRunnerTests` (no image is unreadable).
- [x] `FileCaptureTests`: a screen image, a screen image on a control that exposes text (still the image, never the file), a covered screen image (refused).
- [x] `DesktopFileFieldWalkthroughTests` uses the new type for the TestTarget's image area.
- [x] Docs.
