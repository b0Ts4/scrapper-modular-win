# Prescriva Agent File and Image Fields Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permitir configurar um campo do tipo **arquivo** e capturar, quando o gatilho dispara, o próprio arquivo (receita em PDF/JPG/PNG…) junto do evento — protegido em repouso como o resto dos dados de negócio.

**Decisões do usuário (2026-10-05):** campos do tipo "caminho + Procurar", "imagem exibida na tela" e áreas de arrastar-e-soltar; guardar a **cópia do arquivo original**; limite de **10 MB** por arquivo.

**Architecture:**
- Domain: `FieldDefinition.Kind` (`Text` padrão, `File`). O valor capturado de um campo de arquivo continua sendo uma string no evento: uma referência `attachment:<sha256>`; o conteúdo nunca entra no payload JSON.
- Application: `CaptureResult.Attachment` (bytes, nome, tipo, origem); `IAttachmentStore` (salvar/ler/metadados/remover não referenciados/remover tudo). `SessionCoordinator` grava o anexo **antes** de aplicar a ocorrência e de gravar o evento.
- Infrastructure: `SqliteAttachmentStore` na mesma base, conteúdo cifrado por DPAPI, deduplicado por SHA-256.
- Windows: captura de campo de arquivo:
  1. o controle expõe um caminho existente (ValuePattern/Name) → lê o arquivo (cópia exata, origem `file`);
  2. sem caminho legível e o controle está visível e não coberto → PNG só do retângulo do controle (origem `screen`);
  3. senão → falha tipada visível.
- Limite de 10 MB → falha tipada, nunca evento com anexo truncado.

**Limitação conhecida (aceita):** quando a aplicação só exibe a imagem (ex.: após arrastar-e-soltar), a UI Automation não expõe o arquivo de origem; o melhor possível é a imagem do controle na tela. O payload marca a origem (`file`/`screen`) para não confundir as duas.

## Global Constraints

- Captura só de campos configurados, só quando o gatilho dispara. Captura de tela limitada ao retângulo do controle e **recusada se outra janela o cobre** (nunca registrar conteúdo de outra aplicação).
- Conteúdo de anexo nunca em log, configuração ou diagnóstico; só nome/tamanho/hash na UI do operador.
- Limpeza explícita e retenção também removem anexos (não referenciados por eventos restantes).
- Configurações existentes (só texto) mantêm o mesmo hash de conteúdo — aprovações existentes continuam válidas.

## Review Focus

- Arquivo > 10 MB, caminho inexistente e controle coberto falham de forma tipada e visível, sem evento — Tasks 3–4.
- Os bytes recuperados da fila são idênticos ao arquivo original — Task 5.
- O conteúdo do anexo não aparece em claro no arquivo SQLite nem no log — Tasks 2 e 5.

---

### Task 1: Field kind in the configuration model
- Modify: `FieldDefinition` (`Kind = FieldKind.Text`), `ConfigurationFingerprint` (kind entra no hash só quando ≠ Text), JSON store.
- Tests: JSON round trip com e sem `kind` (configuração antiga carrega como Text); hash de configuração só-texto inalterado; hash muda ao trocar o tipo.

### Task 2: Encrypted attachment store
- Create: `src/Prescriva.Agent.Application/Capture/IAttachmentStore.cs`, `CapturedAttachment`; `src/Prescriva.Agent.Infrastructure/Events/SqliteAttachmentStore.cs`.
- Tests: round trip byte a byte; deduplicação por hash; conteúdo nunca em claro no arquivo; limite de 10 MB recusado; remover não referenciados preserva os referenciados; remover tudo + VACUUM.

### Task 3: File capture on Windows
- Modify: `UiAutomationCaptureProvider` (ramo para `FieldKind.File`), `CaptureResult`.
- TestTarget: "Receita (arquivo):" caixa de caminho + "Procurar…", e área de imagem com arrastar-e-soltar (e um botão determinístico "Mostrar imagem" para os testes).
- Integration tests: caminho → bytes idênticos; arquivo > 10 MB → falha; caminho inexistente → falha; imagem exibida → PNG do tamanho do controle; controle coberto por outra janela → falha.

### Task 4: Runtime, test mode and monitor
- `SessionCoordinator`: grava o anexo e usa a referência como valor; falha de anexo → `CaptureFailed` (rejeita, visível). `IntegrationTestRunner`/test mode mostram "arquivo: nome (tamanho, origem)". Monitor mostra o anexo pelo nome. Limpeza/retenção removem anexos.
- Tests com fakes.

### Task 5: Desktop and end-to-end
- Desktop: tipo do campo (Texto/Arquivo) ao adicionar.
- `MilestoneFlowTests`-style e walkthrough pelo Desktop real: campo de arquivo configurado com o cursor, testado, aprovado, ativado; `item_added` referencia o anexo; bytes recuperados da fila == arquivo original; conteúdo ausente em claro do banco e do log.
- Docs e handoff.
