# Prescriva Agent Spec Gap Closure and Start-with-Windows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fechar as lacunas restantes entre a spec do milestone 1 e o comportamento verificado, e fazer o Agent **iniciar com o Windows**, de forma visível (ícone na bandeja), monitorando as integrações aprovadas sem cliques.

**Pedido do usuário (2026-10-05):** "esse app precisa inicializar automaticamente qnd o pc ligar". OCR foi pedido como capacidade e fica para o plano seguinte (plano 8), sobre a mesma captura de imagem de controle já existente.

**Lacunas da spec cobertas:**
- §9: o evento carrega a **versão da configuração**. Hoje `DomainEvent.ConfigurationVersion` contém a versão do *schema* JSON, que é igual para todas as edições. Passa a carregar também a **revisão de conteúdo**, isto é, o hash de conteúdo que a aprovação já usa.
- §11: falhas transitórias são repetidas **com limites e cancelamento**.
- §8: o modo de teste mostra **sinais correspondentes, duração e avisos de fragilidade**.
- §2/§13: **tray**, com o roteiro manual atualizado.

**Architecture:**
- Domain:
  - `DomainEvent.ConfigurationRevision` (string, opcional). `SessionEngine` recebe a revisão no construtor.
  - `SelectorMatch.RunnerUpScore`, que é a pontuação do segundo melhor candidato.
- Application:
  - `SessionCoordinator` repete resolução e captura apenas para resultados transitórios: elemento ausente, janela ausente, tempo esgotado, elemento indisponível, coberto. A repetição tem tentativas e espera limitadas e respeita cancelamento.
  - Ambíguo, padrão não suportado, arquivo grande ou inexistente nunca são repetidos.
  - `SelectorResolution.Evidence` e `.Lead`.
  - `SelectorFragility`: avisos puros e testáveis.
  - `FieldCheckResult` ganha sinais, duração e avisos; `TriggerCheckResult` ganha duração.
  - `StartupActivation`: retoma a integração que o operador deixou ativa (`IMonitoringPreferenceStore`, gravada ao Ativar e limpa ao Parar), somente se `Approved`. O monitor do Desktop ativa uma integração por vez.
  - `IStartupRegistration`.
- Infrastructure: a coluna `configuration_revision` é adicionada por migração do schema da fila para a versão 2. Bancos v1 existentes continuam abrindo.
- Desktop:
  - Instância única por diretório de dados: uma segunda execução pede à primeira que se mostre e encerra.
  - Ícone na bandeja: mostrar, parar monitoramento, sair. Fechar a janela durante o monitoramento esconde na bandeja em vez de parar silenciosamente.
  - Opção "Iniciar com o Windows", gravada em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, que inicia `--background`.
  - `--background`: começa só na bandeja e retoma a integração deixada ativa, se ainda aprovada para o seu conteúdo; caso contrário mostra o motivo. Uma integração não aprovada nunca é ativada.

**Por que logon, e não serviço do Windows:** a UI Automation precisa da área de trabalho interativa do usuário, e a DPAPI está ligada ao usuário. Um serviço na sessão 0 não enxerga o ERP. Por isso o Agent usa a chave `Run` do usuário atual, que não exige administrador.

## Global Constraints

- Operação explícita e visível: em segundo plano, o ícone na bandeja informa que está monitorando. Iniciar com o Windows é opt-in, desligado por padrão.
- Nada não aprovado é ativado automaticamente; aprovação alterada (`ChangedSinceTest`) também não.
- Repetições não podem capturar um valor de outro elemento: a resolução completa é refeita a cada tentativa. Ambiguidade nunca é repetida.
- Configurações e filas existentes continuam válidas: o hash de aprovação não muda e o banco v1 é migrado no lugar.

## Review Focus

- A migração v1→v2 preserva eventos existentes — Task 1.
- Uma falha não transitória não é repetida, e o cancelamento interrompe a espera — Task 2.
- Duas instâncias nunca monitoram o mesmo diretório de dados (eventos duplicados) — Task 4.
- `--background` não ativa uma configuração não aprovada nem alterada — Task 4.

---

### Task 1: Content revision in events
- [x] RED: `SessionEngineTests`: um evento emitido carrega a revisão passada ao motor.
- [x] RED: `SqliteEventOutboxTests`:
  - a revisão é gravada e lida;
  - um banco criado com o schema v1 (sem coluna) abre, mantém os eventos e passa a aceitar a revisão.
- [x] RED: `SessionCoordinatorTests`: os eventos gravados carregam `ConfigurationFingerprint.Compute(configuration)`.
- [x] GREEN: `DomainEvent.ConfigurationRevision`; `SessionEngine(configuration, revision)`; `OutboxSchema` v2 com `ALTER TABLE` condicional; o coordenador passa o hash.

### Task 2: Bounded retry of transient capture failures
- [x] RED: `SessionCoordinatorTests`:
  - um campo NotFound na 1ª tentativa e Found na 2ª produz o evento;
  - um campo Ambiguous é resolvido uma única vez;
  - sempre NotFound esgota as tentativas e rejeita com `CaptureFailed`;
  - captura `ElementUnavailable` → `Captured` produz o evento;
  - `TooLarge` não é repetido;
  - o cancelamento durante a espera termina sem evento.
- [x] GREEN: `CaptureRetryPolicy` com 3 tentativas e esperas de 100 e 200 ms, injetável. Cada repetição gera a entrada de log técnico `field_capture_retried`, sem valor.

### Task 3: Test-mode evidence — signals, duration, fragility
- [x] RED (Domain): `SelectorMatcherTests`: `RunnerUpScore` é o segundo melhor, ou nulo com um candidato.
- [x] RED (Application): `SelectorFragilityTests` para:
  - sem AutomationId e sem rótulo, ou seja, dependente de nome/posição;
  - confiança < 0,8;
  - margem curta (lead < 2× mínimo);
  - correspondência só por posição;
  - um seletor forte (AutomationId) sem avisos.
- [x] RED: `IntegrationTestRunnerTests`: o resultado do campo traz sinais, duração e avisos; o resultado do gatilho traz a duração até a detecção.
- [x] GREEN: resolver UIA preenche `Evidence`/`Lead`. O modo de teste mostra "Sinais: …", "Tempo: N ms" e "Aviso: …".
- [x] Integration (Windows): no walkthrough estrutural, o campo só com rótulo mostra os sinais `nearbyLabels` e o aviso de ausência de AutomationId; um campo com AutomationId não mostra aviso.

### Task 4: Single instance, tray, start with Windows, background activation
- [x] RED (Application): `StartupActivationTests`: retoma a integração deixada ativa só se `Approved`; nenhuma ativa, `NotTested`, `ChangedSinceTest` e configuração ilegível não ativam nada, cada uma com motivo visível.
- [x] RED (Windows integration): `RunKeyStartupRegistration` grava e remove o valor numa subchave de teste, com o comando entre aspas e `--background`.
- [x] GREEN: implementação; checkbox "Iniciar com o Windows"; `NotifyIcon` (WinForms) com menu; instância única por diretório de dados (`Mutex` + `EventWaitHandle` com nome derivado do hash do diretório).
- [x] E2E (Desktop real):
  - com uma configuração aprovada no diretório de dados, `Desktop.exe --background` não mostra janela e captura `item_added` ao clicar em Adicionar no TestTarget, sem nenhum clique no Agent;
  - uma configuração alterada após a aprovação não é ativada;
  - uma segunda execução mostra a janela da primeira e encerra; o total de eventos continua 1 por clique.

### Task 5: Docs and handoff
- [x] `docs/testing/milestone-1-manual.md`: linhas para a bandeja, para iniciar com o Windows (reiniciar a sessão) e para a evidência do modo de teste.
- [x] Arquitetura (event-engine, security), roadmap, `docs/testing.md`, handoff, plano corrente.
