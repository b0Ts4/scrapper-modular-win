# Prescriva Agent CSV Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** let the operator export the captured events to a CSV file that opens correctly in Excel (pt-BR) and other spreadsheets.

**Request (2026-10-08):** "preciso que seja possível exportar os dados capturados, pode ser via csv".

## Design

- **Application** — `EventCsvExporter` (pure, no I/O besides a `TextWriter`):
  - one row per event, in sequence order;
  - fixed columns `sequencia;data_hora;evento;integracao;sessao;itens`, then one column per captured field, in the order fields first appear;
  - the `itens` column is the number of items the session had confirmed at that event (an `item_added` counts its own item; `budget_finished` gives the total); each item is also its own `item_added` row;
  - `;` separator (Excel pt-BR), CRLF line ends, RFC 4180 quoting;
  - **formula-injection guard**: a value starting with `=`, `+`, `-`, `@`, tab or CR is prefixed with `'`, unless it is a plain number such as `-5` or `+3,5`;
  - file and image fields show a description (file name), never the internal `attachment:` reference.
- **Application / Infrastructure** — `IEventOutbox.ReadExportableAsync`:
  - returns the pending and the delivered (confirmed) events still kept, in append order; never the quarantined ones;
  - the interface default is the pending events; `SqliteEventOutbox` also includes the confirmed ones.
- **Desktop**:
  - step 5 gets *Exportar eventos (CSV)...*: a save dialog suggesting `eventos-<integração>-<data>.csv`, a UTF-8 file with BOM, and a status giving the count and warning that the CSV is **not encrypted**;
  - disabled when there are no events;
  - `RuntimeMonitorViewModel.ExportCsvAsync` does the work.

## Privacy

- Exporting is an explicit, visible operator action; the file goes only where the operator chooses.
- The technical log never receives values (it records nothing for the export).
- The configuration JSON is unchanged.

### Task 1: Exporter
- [x] RED/GREEN `EventCsvExporterTests`:
  - header and columns;
  - field columns in first-appearance order with blanks where an event lacks a field;
  - quoting of `;`, `"` and line breaks;
  - formula guard (and plain negative numbers kept);
  - attachment description;
  - `itens` count.

### Task 2: Exportable events
- [x] RED/GREEN `SqliteEventOutboxTests`: pending and confirmed events are exported in order, quarantined ones are not.

### Task 3: Desktop
- [x] RED/GREEN `RuntimeMonitorViewModelTests`: `ExportCsvAsync` writes a UTF-8 CSV with BOM and returns the count.
- [x] Button in step 5; `DesktopWalkthroughTests` exports through the real save dialog and reads the CSV back.
- [x] Docs: manual row, testing, handoff.
