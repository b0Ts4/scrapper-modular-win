# Prescriva Agent Microsoft Store Packaging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Distribuir o Agent pela Microsoft Store como pacote MSIX. A Store assina o app com o certificado da Microsoft, hospeda e atualiza automaticamente, sem aviso do SmartScreen e sem certificado próprio.

**Decisões do usuário (2026-10-06):**
- Distribuição pela Microsoft Store.
- Publicador: pessoa física.

**Por que a Store:**
- O Azure Artifact Signing não atende organizações no Brasil (só EUA, Canadá, UE e Reino Unido; pessoas físicas só EUA e Canadá).
- Um certificado OV custa cerca de US$ 200–350/ano e exige chave em hardware ou HSM na nuvem.
- O registro na Store é gratuito.

**Architecture:**
- **Pacote:** MSIX full trust (`runFullTrust`), publicado self-contained win-x64. Contém o `AppxManifest` e os logos. Script `packaging/build-msix.ps1`: publicar → montar o layout → `makeappx pack` → assinar opcionalmente com um `.pfx` (só para testes de sideload, porque na Store quem assina é a Microsoft).
- **Identidade:** `Name`, `Publisher` e `PublisherDisplayName` vêm de parâmetros, com placeholders até o usuário reservar o nome no Partner Center.
- **Iniciar com o Windows dentro do pacote:**
  - Um app empacotado não pode usar a chave `Run`, porque as gravações em HKCU são virtualizadas. Usa a extensão `desktop:StartupTask` e a API `Windows.ApplicationModel.StartupTask`.
  - `IStartupRegistration` passa a ser assíncrona e informa o estado, inclusive "desativado pelo usuário no Gerenciador de Tarefas", que o app não pode reativar sozinho.
  - O Desktop escolhe a implementação conforme o processo rode empacotado ou não.
- **Modo segundo plano:** acionado por `--background` (chave `Run`) ou por ativação do tipo `StartupTask` (pacote). A decisão é uma função pura testável.
- **Dados:** dentro do pacote, `%LOCALAPPDATA%` é redirecionado para a pasta privada do pacote, e desinstalar remove os dados locais. Isso entra na política de privacidade.

## Global Constraints

- Nada muda para quem roda sem pacote: a chave `Run` e o `--background` continuam como estão.
- Iniciar com o Windows continua opt-in e visível na bandeja.
- A política de privacidade descreve exatamente o que o app captura, onde guarda (local, criptografado), retenção, limpeza, que nada é enviado nesta versão e que o OCR é local.

## Review Focus

- Dentro do pacote, a opção usa `StartupTask`, nunca a chave `Run` (que seria inócua) — Task 1.
- Um pacote montado pelo CI instala, abre com identidade de pacote e liga/desliga a inicialização automática — Task 2.

---

### Task 1: Startup registration that works packaged and unpackaged
- [x] RED:
  - `StartupModeTests`: `--background` → background; ativação `StartupTask` → background; ativação normal → janela.
  - `RunKeyStartupRegistrationTests` adaptados à API assíncrona com estado.
- [x] GREEN:
  - `IStartupRegistration` assíncrona com `StartupRegistrationState` (`Enabled`, `Disabled`, `DisabledByUser`, `DisabledByPolicy`, `EnabledByPolicy`);
  - `RunKeyStartupRegistration`, `PackagedStartupRegistration` (`StartupTask`), `PackageIdentity.IsPackaged`;
  - `App` usa `StartupMode`; o Desktop mostra a mensagem certa para cada estado.

### Task 2: MSIX package built, installed and started by CI
- [x] `packaging/AppxManifest.xml` (template), logos, `packaging/build-msix.ps1`.
- [x] CI:
  - gera um certificado de teste, monta e assina o `.msix` e o publica como artefato;
  - instala com `Add-AppxPackage`;
  - roda `PackagedAgentTests`: o app abre pela AUMID com identidade de pacote; marcar "Iniciar com o Windows" registra a `StartupTask` (estado `Enabled`) e desmarcar desativa; reabrir mostra o estado salvo.
- [x] Desinstala no fim.

### Task 3: Store readiness
- [x] `docs/privacy-policy.md` (pt-BR e en), `docs/release/microsoft-store.md`: passo a passo no Partner Center, identidade, upload, idade, categoria, *declarações de capacidade* (`runFullTrust`), público oculto (link privado).
- [x] Texto da listagem (pt-BR).

### Task 4: Docs and handoff
- [x] Setup (instalação pela Store, sideload de teste), arquitetura, roteiro manual (instalar pela Store, iniciar com o Windows empacotado), handoff.
