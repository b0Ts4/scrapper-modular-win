# Prescriva Agent Innermost Element Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** let the operator mark a small field that sits inside a larger one.

**Request (2026-10-09):** "quando tem um campo maior por fora, eu não consigo selecionar os campos menores internos; se quando apertasse Shift pudesse marcar os menores".

**Cause:**
- Hit-testing stops at the outer element when the program does not expose the inner ones to the mouse (cards, panels, custom drawing).
- The inspector deliberately promotes an anonymous text/image inside a button or list item to that container.

## Design

- **Application** — `InspectionDepth { Interactive, Innermost }`:
  - `IElementInspector.FromPointAsync(point, depth, …)`, whose default implementation is interactive;
  - `InspectionController.ObservePointerAsync(point, depth)`.
- **Windows** — `Innermost` returns the smallest on-screen element containing the point among the hit element and its descendants:
  - ties go to the deeper element;
  - no promotion;
  - above 3000 descendants the hit element is kept.
- **Desktop**, while marking:
  - **Shift held** → innermost; the hover text says "[Shift: elemento interno]";
  - **Ctrl pressed** while the other program is in front → confirms the outlined element, so the mouse never has to travel back over other elements;
  - tips are shown in steps 2 and 3.
- **Privacy** — only the up/down state of Shift and Ctrl is read (`GetAsyncKeyState`), and only while marking is on and visible. Nothing else is read from the keyboard.

### Tasks
- [x] RED/GREEN `InspectionControllerTests`: the requested depth reaches the inspector; the default stays interactive.
- [x] `ElementInspectionTests`:
  - TestTarget's `ProductPrice`, inside `ProductCard` and not hit-testable, is reported as the card normally and as the price when innermost;
  - innermost never promotes a caption to its button.
- [x] `DesktopInnermostSelectionTests`: real Shift/Ctrl input:
  - without Shift → card;
  - with Shift → price, with the outline around the price;
  - Ctrl → confirmed; then added as "Preço" (`preco`).
- [x] Docs.
