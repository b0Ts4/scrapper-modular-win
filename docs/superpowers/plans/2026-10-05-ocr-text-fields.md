# Prescriva Agent OCR Text Fields Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ler como texto um campo que a UI Automation não expõe (texto desenhado, receita digitalizada exibida como imagem, controle sem padrão de valor), reconhecendo o texto da imagem do próprio controle na tela.

**Pedido do usuário (2026-10-05):** "E o ocr também está funcionando blzinha? Já é interessante ter a possibilidade dele." A spec do milestone 1 (§7) já prevê "captura de região/OCR por meio do mesmo contrato".

**Architecture:**
- **Domain:** `FieldKind.OcrText`. O valor capturado é texto comum, e a sessão e os eventos não mudam. O hash de conteúdo só muda para campos não-texto, como já acontece hoje.
- **Windows:**
  - `ControlImageCapture` é extraído de `FileFieldCapture`: PNG do retângulo do controle, recusado se algo o cobre.
  - `OcrFieldCapture` reconhece o texto com o OCR nativo do Windows (`Windows.Media.Ocr`): offline, sem dependência externa, sem enviar a imagem a lugar nenhum.
  - Idioma: pt-BR, se instalado; senão o português disponível; senão o primeiro idioma do perfil do usuário com reconhecedor.
  - Imagens pequenas são ampliadas antes do reconhecimento.
- **Resultados:**
  - Texto reconhecido: valor com as linhas preservadas e espaços normalizados; provedor `windows-ocr`.
  - Sem reconhecedor instalado: falha tipada `OcrUnavailable`, que não é repetida.
  - Controle coberto: `Obscured` (transitório, repetido pela política do plano 7).
  - Nada reconhecido: valor vazio, e um campo obrigatório rejeita o evento.
- **Projetos com WinRT:** os projetos que usam ou referenciam `Prescriva.Agent.Windows` passam a mirar `net10.0-windows10.0.19041.0` (Windows 10 2004 ou superior), condição para as projeções WinRT do OCR.

## Global Constraints

- Privacidade igual à dos campos de imagem: só o retângulo do controle configurado, só quando o gatilho dispara, recusado se coberto. A imagem é descartada após o reconhecimento e não é guardada. O texto reconhecido é um valor capturado: nunca vai para o log técnico.
- Nenhum serviço de OCR em nuvem.

## Review Focus

- Um controle coberto nunca é lido (o texto de outra janela nunca vira valor) — Task 2.
- A ausência de reconhecedor é uma falha visível e tipada, nunca um valor vazio silencioso — Tasks 2–3.

---

### Task 1: Field kind and target framework
- [ ] RED: `FieldKindTests`: `ocrText` faz round trip em JSON; o hash de uma configuração só-texto continua igual (o teste de caracterização já existe); trocar Texto→OCR muda o hash.
- [ ] GREEN: `FieldKind.OcrText`. TFM `net10.0-windows10.0.19041.0` para Windows, Desktop e projetos de teste que os referenciam. Os caminhos de executável nos testes são atualizados.

### Task 2: OCR capture on Windows
- [ ] TestTarget: área "Receita digitalizada:" (`ScannedPrescriptionImage`), uma imagem sem texto acessível, com os botões determinísticos "Mostrar receita digitalizada" (`DIPIRONA 500 MG`) e "Outra receita" (`AMOXICILINA 875 MG`).
- [ ] RED (integração): `OcrCaptureTests`:
  - o texto da imagem é reconhecido;
  - trocar a imagem muda o valor;
  - o controle coberto por outra janela é `Obscured`;
  - o resultado traz o idioma usado.
- [ ] RED (unidade): normalização das linhas; escolha do idioma (pt-BR > pt > perfil > primeiro).
- [ ] GREEN: `ControlImageCapture`, `OcrFieldCapture`, ramo `OcrText` no `UiAutomationCaptureProvider`, `CaptureOutcome.OcrUnavailable`.

### Task 3: Runtime, test mode, Desktop and end-to-end
- [ ] RED: `SessionCoordinatorTests`: `OcrUnavailable` rejeita com `CaptureFailed` sem repetir. Mensagem do modo de teste para OCR indisponível.
- [ ] GREEN: mapeamento no coordenador e no runner; tipo "Text via OCR" no Desktop.
- [ ] E2E (`DesktopOcrFieldWalkthroughTests`): o campo OCR é selecionado com o cursor real, testado ("Valor lido: DIPIRONA 500 MG"), aprovado e ativado; Adicionar grava `item_added` com o texto reconhecido; outra receita grava o outro texto; o texto não aparece no log técnico.

### Task 4: Docs and handoff
- [ ] Arquitetura (capture-engine, security), roteiro manual (OCR, inclusive com pt-BR instalado), roadmap, setup (instalar o idioma de OCR), handoff.
