# Prescriva Windows Agent

Agente Windows configurável para coleta autorizada de dados de aplicações desktop. O primeiro milestone valida o fluxo com um aplicativo controlado, `Prescriva.Agent.TestTarget`: configuração visual, reencontro de elementos, captura de campos configurados, gatilhos e eventos locais.

O projeto está no início da implementação. Esta solution contém as camadas Domain, Application e Infrastructure e seus projetos de teste. O Inspector, o TestTarget e o fluxo funcional virão nas próximas tarefas dos [planos](docs/plans/current-plan.md).

## Começar

Instale o .NET 10 SDK e execute:

```powershell
dotnet build Prescriva.Agent.slnx --configuration Release
dotnet test Prescriva.Agent.slnx --configuration Release --no-build
```

Consulte [setup](docs/setup.md) para os pré-requisitos e [testing](docs/testing.md) para a estratégia de testes. As decisões arquiteturais estão em `docs/decisions/`, e o estado atual em [current-state](docs/handoffs/current-state.md).

O Agent deve operar de forma explícita e visível, capturando somente elementos configurados pelo usuário. Não há captura indiscriminada da área de trabalho, keylogger ou leitura global da área de transferência.
