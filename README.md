# Prescriva Windows Agent

Agente Windows configurável para coleta autorizada de dados de aplicações desktop. O primeiro milestone valida o fluxo com um aplicativo controlado, `Prescriva.Agent.TestTarget`: configuração visual, reencontro de elementos, captura de campos configurados, gatilhos e eventos locais.

O fluxo do primeiro milestone está implementado e verificado automaticamente em Windows (CI `windows-latest`): configurar campos e botões visualmente no `Prescriva.Agent.Desktop`, salvar/recarregar, testar contra o TestTarget em execução, aprovar, ativar e gravar `item_added`/`budget_finished` numa fila SQLite protegida por DPAPI. O roteiro manual com pessoa e mouse ainda tem itens visuais em aberto — veja [current-state](docs/handoffs/current-state.md).

Visão geral da arquitetura: [docs/architecture/overview.md](docs/architecture/overview.md). Próximos passos: [roadmap](docs/roadmap.md).

## Começar

Instale o .NET 10 SDK e execute:

```powershell
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release --no-build
```

Para usar o Agent: abra `Prescriva.Agent.TestTarget.exe` e `Prescriva.Agent.Desktop.exe` (pastas `bin\Release\net10.0-windows` e `bin\Release\net10.0-windows10.0.19041.0`, respectivamente) e siga o [roteiro do milestone](docs/testing/milestone-1-manual.md).

Consulte [setup](docs/setup.md) para os pré-requisitos e [testing](docs/testing.md) para a estratégia de testes. As decisões arquiteturais estão em `docs/decisions/`, e o estado atual em [current-state](docs/handoffs/current-state.md).

O Agent deve operar de forma explícita e visível, capturando somente elementos configurados pelo usuário. Não há captura indiscriminada da área de trabalho, keylogger ou leitura global da área de transferência.
