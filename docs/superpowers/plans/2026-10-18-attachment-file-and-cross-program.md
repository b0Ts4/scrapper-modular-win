# Plan 16 — The whole prescription file, and one extraction across two programs (2026-10-18)

## Why

- The attendant usually sees only part of the prescription image, and the Agent must not touch the screen (no scrolling or zooming the viewer): a screen capture can only hold what is drawn.
- The prescription fields (dosage, prescriber, CRM, components - Orçamentos screen, `FCReceitas.exe`) and the prescription file (attachment viewer, `FCMobyPharma.exe`) must arrive in **one** event.

Observed on the operator's PC (names, sizes and times only): every time an attachment is opened, MobyPharma writes the original file to the user's temp folder as `anexo_YYYYMMDD_HHMMSSmmm.<ext>` (PDF, image or audio) - a new file per opening, so the newest one is the attachment on screen.

## Design

1. **Field kind `RecentFile`** ("arquivo aberto pelo programa"): the field still points at an element (the viewer's image area), which must be found - the attachment is open -, and carries a `FileSource` (folder with environment variables, file pattern, maximum age). The capture copies the newest matching file in the folder or its direct subfolders (the per-session `Temp\<n>`), not older than the maximum age, up to 10 MB, as an attachment with its original name. Nothing on screen is touched; the file is read only when the button is clicked, and only that file.
2. **One integration, two programs**: the integration's program is the main one (session per instance, as today); a field or button whose selector names another process is resolved and watched in that process (any instance) instead of the main instance. Routing lives in Application (`ProcessRoutingSelectorResolver`, `ProcessRoutingTriggerProvider`), composed in the Windows factories; test mode and runtime both use it.
3. **Button roles in templates**: *capture and send* (capture its fields, emit, clear - the default), *capture only* (keep the values in the session for a later send), *discard* (clear). The template's OK sends; *Nova Fórmula* / *Novo Produto* capture the file only; *Cancelar* discards.
4. **Templates across programs**: an item may name its own program (process + part of the title); its suggestion is aimed at the open window of that program.
5. Shipped template *FórmulaCerta – Receita completa (anexo + orçamento)*: attachment file (MobyPharma, kept when *Nova Fórmula*/*Novo Produto* is clicked), dosage, prescriber council/UF/number/name, components (OCR text + image), *OK* sends one `receita` event, *Cancelar* discards.

## Tasks (test first)

1. Domain: `FieldKind.RecentFile`, `FileSource`, validation, content hash (only when present), JSON.
2. Windows: newest-file finder; capture of a `RecentFile` field.
3. Application + Windows: process routing for resolution and triggers.
4. Templates: per-item program, file source and button role; guide; shipped template.
5. Walkthrough on FórmulaCerta (no saving), docs.

## Result (2026-10-18)

Tasks 1–4 done, test first. Main program of the shipped template: MobyPharma's order window (open all day), so the session exists before *Nova Fórmula* is clicked; the attachment viewer is a window of the same process, the Orçamentos screen another program. On FórmulaCerta: the attachment file item copied the whole original PDF (43 KB) via its suggested position. Not tested there: the *Nova Fórmula*/*Novo Produto* click (disabled on the open order) and the Orçamentos items (screen not open) - the end-to-end `receita` event is still to be seen on the operator's PC.

## Privacy

Only the newest file matching the configured pattern, in the configured folder, when a configured button is clicked; stored encrypted like every attachment; never logged. The folder and pattern are part of the integration and visible in the checklist.
