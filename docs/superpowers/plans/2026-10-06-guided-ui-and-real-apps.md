# Prescriva Agent Guided UI and Real Applications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Tornar o Agent usável por quem não é técnico e provar que ele funciona em aplicativos reais do Windows, não só no TestTarget.

**Problema relatado (2026-10-06):**
- A UI é um formulário longo com 8 seções, IDs digitados à mão (inclusive "capturar campos" separados por vírgula), processo e título da janela digitados de memória, e nenhuma lista de programas abertos.
- Quase tudo foi testado só contra o TestTarget.

**Architecture:**
- **Janelas abertas:**
  - Application define `OpenWindowInfo` (nome do app, título, processo, PID), `IOpenWindowSource` e `OpenWindowFilter` (busca sem acento e sem diferenciar maiúsculas).
  - Windows implementa `Win32OpenWindowSource`: janelas de nível mais alto visíveis, com título, que não sejam do próprio Agent.
  - Para apps UWP/Store hospedados no `ApplicationFrameHost`, o processo informado é o do **conteúdo**.
  - A lista fica só em memória e nunca é registrada em log.
- **Identidade de apps UWP:** a hipótese a verificar é que a janela de nível mais alto pertence ao `ApplicationFrameHost` e o conteúdo a outro processo (ex.: `CalculatorApp`). Se confirmada:
  - `AutomationWindowLocator`, a descoberta de instâncias e o inspector passam a identificar o app pelo processo do conteúdo;
  - a janela continua sendo a moldura do `ApplicationFrameHost`.
  - Nunca escolhem um candidato ambíguo em silêncio.
- **IDs a partir do nome amigável:** `SemanticIdGenerator` (Application) gera um slug válido (`^[A-Za-z][A-Za-z0-9_]*$`), sem acentos e único sem diferenciar maiúsculas. O ID não aparece para o usuário comum.
- **Desktop:**
  - `MainWindow` vira um assistente de 5 passos: 1 Programa → 2 Campos → 3 Botões → 4 Testar → 5 Ativar.
  - Navegação lateral e textos simples por passo; listas de campos e gatilhos com editar/remover; os campos capturados por um gatilho são escolhidos por caixas de marcação.
  - A digitação manual de processo e janela continua disponível como opção avançada.
  - Todo o comportamento existente é preservado.
- **Apps reais:**
  - testes de integração com Calculadora (`CalculatorResults`, `num*Button`, `equalButton`) e Bloco de Notas;
  - só por AutomationId e independentes de idioma;
  - ignorados com motivo explícito quando o app não existe na máquina;
  - um passeio completo pela UI nova contra a Calculadora.

## Global Constraints

- **Privacidade:** a lista de janelas tem só app, título, processo e PID, fica em memória e não vai para log nem para disco. Nenhum conteúdo de janela não configurada é lido.
- **Camadas:** Domain continua puro; Application expõe interfaces; Windows e Desktop implementam a parte de desktop.
- **Falhas:** visíveis e tipadas; sem seletor ambíguo; nenhum evento com campo obrigatório faltando.

## Review Focus

- O teste da Calculadora reproduz a hipótese do `ApplicationFrameHost` antes da correção — Task 2.
- Os testes de ponta a ponta continuam provando o mesmo comportamento após o redesenho (nenhuma asserção enfraquecida) — Task 4.

---

### Task 1: Probe the runner and list open windows
- [x] CI: `scripts/probe-real-apps.ps1` informa se Calculadora, Bloco de Notas e Paint existem e como são as janelas e os AutomationIds.
- [x] RED/GREEN:
  - `OpenWindowFilterTests`: busca ignora acento e maiúsculas e procura em app, título e processo;
  - `SemanticIdGeneratorTests`: slug válido, sem acento, prefixo quando começa com número, único;
  - `OpenWindowSourceTests` (integração): o TestTarget aparece com título, processo e PID; a janela do Agent não aparece; janelas sem título ou invisíveis não aparecem.

### Task 2: Real UWP apps (Calculator)
- [x] RED: `CalculatorTests` (integração) — a Calculadora aparece na lista com o processo do conteúdo; o seletor `CalculatorResults` é encontrado e lido; o gatilho `equalButton` é detectado; a descoberta de instâncias acha a Calculadora.
- [x] GREEN: locator, descoberta de instâncias, inspector e lista entendem `ApplicationFrameHost`.
- [x] `NotepadTests`: o campo de texto é lido.
- [x] Ignorados com motivo quando o app não existe.

### Task 3: Guided configurator
- [x] `SemanticIdGenerator` no editor; listas de campos e gatilhos; caixas de marcação de campos a capturar.
- [x] `MainWindow` como assistente de 5 passos, mantendo os AutomationIds onde fizer sentido.
- [x] `DesktopDriver` e os testes de ponta a ponta atualizados para o novo fluxo, com as mesmas asserções de comportamento.

### Task 4: Real-app walkthrough and docs
- [x] `DesktopCalculatorWalkthroughTests`: escolher a Calculadora na lista, marcar o display e o botão `=`, testar, aprovar, ativar e gerar o evento com o valor do display.
- [x] Screenshots da nova UI; roteiro manual (apps reais e limitações: Electron, Java, canvas); handoff; README e setup.

## Result (2026-10-07)

- The CI runner has no Store Calculator. The UWP-specific test skips with that reason, and `DesktopCalculatorWalkthroughTests` runs against the classic `win32calc` instead. Human verification of the Store Calculator is manual row 30.
- Bugs found and fixed:
  - pattern-less labels were not captured (RED/GREEN on CI);
  - white step text after the redesign (found by screenshot review).
