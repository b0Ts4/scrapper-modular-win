# FórmulaCerta prescription event — how to read the components

Applies to the shipped templates *FórmulaCerta – Receita completa*, *Orçamento de fórmula* and *Receita*.

## Fields

| Field id | Kind | Use |
| --- | --- | --- |
| `anexo_receita` | attachment (original file) | The whole prescription (PDF/image) — *Receita completa* only. |
| `posologia`, `prescritor_conselho`, `prescritor_uf`, `prescritor_crm`, `prescritor_nome` | text | Read directly from the controls: exact. |
| `quantidade`, `quantidade_unidade` | text | The formula's *Volume* and its unit: for capsules, the number of capsules (e.g. `90,00` + `CAP`). |
| `qtd_prescrita`, `qtd_potes` | text | *Qtd.Prescrita* and *Qtd. Potes*. |
| `numero_orcamento`, `serie_formula` | text | The quote number and which formula of the quote this is (*Orçamento* and *Receita completa* only). In a new quote the number may only exist after saving - to be confirmed. |
| `componentes_imagem` | attachment (PNG of the grid) | **The source of the components** (code, description, quantity, unit). |
| `componentes_ocr` | text | OCR of the same grid, one line per row, cells joined with ` \| `. **Cross-check only.** |

## One event per formula

A quote can have several formulas: after *OK* FórmulaCerta asks whether there are more formulas, then closes the screen. In *Receita completa* the *OK* button has the role *send and keep*: each *OK* sends one `receita` event for that formula and keeps the prescription file, so every formula of the quote carries it. The file is replaced on the next *Nova Fórmula*/*Novo Produto* and thrown away on *Cancelar*. Group the events by `numero_orcamento`. Caveat: a quote started without *Nova Fórmula*/*Novo Produto* (e.g. from the Receitas screen) would carry the last kept file - check that the file matches the quote.

## Why the image is the source

FórmulaCerta's component grid (`TwwDBGrid`) exposes no cells to UI Automation, so its text can only come from the screen. Windows OCR on the real grid (2026-10-18, quote opened in consult mode) read the header correctly but the data row with errors: a digit dropped from the code, `LI` read as `U` in the drug name, the decimal comma lost, and the unit and value columns missing. Enlarging, grey-scale and black-and-white variants did not fix it. A silent error in a dose is worse than an empty field, so the OCR text must never be used as the final value.

## Recommended reading on the receiving side

1. Transcribe the components from `componentes_imagem` (a vision model), one row per component: code, description, quantity (decimal comma), unit.
2. Compare each row with the corresponding `componentes_ocr` line. When the code, description or quantity disagree, mark the row **"conferir"** for a person instead of choosing one.
3. If `componentes_imagem` is missing, treat the components as unknown — do not fall back to the OCR text.

Limit: only the rows visible in the grid are in the image; a formula with more components than fit in the grid arrives incomplete. An exact source (read-only lookup of the quote's items by its number) is a possible later plan.
