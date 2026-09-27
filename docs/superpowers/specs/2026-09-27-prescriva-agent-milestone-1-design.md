# Prescriva Windows Agent — Design do primeiro milestone

**Data:** 2026-09-27  
**Status:** aprovado
**Plataformas:** Windows 10 e Windows 11, x64

## 1. Objetivo

Construir o primeiro fluxo funcional de um agente Windows genérico e configurável para coleta autorizada de dados de aplicações desktop. Uma integração deve ser criada visualmente e armazenada como dados declarativos, sem código específico para cada ERP.

O primeiro milestone provará o fluxo completo usando uma aplicação controlada, `Prescriva.Agent.TestTarget`: selecionar campos e botões, gerar seletores resilientes, salvar a configuração, reencontrar os elementos, detectar gatilhos, capturar valores e produzir eventos de domínio visíveis no modo de teste.

O Agent opera em modo explícito e visível. Captura somente elementos configurados pelo usuário. Não implementa keylogger, leitura global de clipboard, monitoramento oculto ou captura indiscriminada da área de trabalho.

## 2. Escopo

### Incluído

- aplicação desktop em C# e WPF;
- detecção de processo e janela;
- Inspector visual para campos e botões;
- overlay transparente para o mouse;
- fingerprint composto de elementos UI Automation;
- busca híbrida exata e por pontuação;
- captura de campos individuais via UI Automation;
- múltiplos gatilhos e etapas lineares;
- ações declarativas de captura, transição e emissão de eventos;
- eventos `item_added` e `budget_finished`;
- configuração JSON versionada;
- fila local de eventos em SQLite;
- proteção de payloads de negócio com Windows DPAPI;
- modo de teste com confiança e diagnóstico;
- TestTarget e testes automatizados apropriados.

### Fora do primeiro milestone

- OCR e visão computacional;
- integração com backend remoto;
- MSAA e providers Win32 adicionais;
- tabelas complexas e paginação;
- fluxos condicionais arbitrários;
- instalador e atualizador de produção;
- garantia de compatibilidade com todos os ERPs.

Esses itens permanecem no roadmap. As interfaces do núcleo devem permitir acrescentar providers e transporte remoto sem modificar as regras de domínio.

## 3. Decisões tecnológicas

O configurador será implementado em WPF sobre .NET moderno para Windows. WPF oferece acesso direto às APIs de desktop, suporte maduro a janelas transparentes e integração adequada com UI Automation, tray e Win32. O núcleo não dependerá de WPF.

O Agent será inicialmente x64. A capacidade do UI Automation de inspecionar aplicações de arquiteturas diferentes será validada nos testes Windows. Código que acessa UI Automation será executado fora da thread da interface, em thread dedicada e com ciclo de vida explícito.

## 4. Organização da solution

- `Prescriva.Agent.Domain`: configurações tipadas, sessões, etapas, ações e eventos. Não depende de Windows, WPF ou persistência.
- `Prescriva.Agent.Application`: coordena inspeção, resolução, captura, gatilhos, transições e armazenamento por interfaces.
- `Prescriva.Agent.Windows`: processos, janelas, UI Automation, cursor, hooks e overlay.
- `Prescriva.Agent.Infrastructure`: JSON, SQLite, DPAPI, relógio, IDs e logs.
- `Prescriva.Agent.Desktop`: WPF, configurador, Inspector e modo de teste.
- `Prescriva.Agent.TestTarget`: aplicação WPF que simula um ERP.
- projetos de testes correspondentes às camadas.

As dependências apontam para dentro: Desktop, Windows e Infrastructure dependem de contratos de Application e tipos de Domain; Domain não conhece as demais camadas.

## 5. Modelo declarativo

Uma integração versionada contém:

- identidade do aplicativo e regras de correspondência da janela;
- campos, tabelas e regiões;
- fingerprints e providers permitidos;
- etapas do fluxo;
- gatilhos, condições e ações;
- normalizações simples;
- versão do schema.

As quatro primitivas do produto são `Field`, `Table`, `Region` e `Trigger`. O primeiro milestone implementa `Field` e `Trigger`; mantém tipos e contratos preparados para evoluir as outras primitivas sem colocar detalhes de OCR no domínio.

Cada gatilho referencia um seletor, o evento observado, a etapa em que é válido e uma lista ordenada de ações. As ações iniciais são:

- capturar campos;
- emitir evento;
- avançar ou voltar de etapa;
- limpar estado acumulado;
- finalizar ou cancelar uma sessão.

Mais de um elemento pode produzir a mesma ação sem duplicar a definição dos campos ou do evento.

## 6. Selector Engine

Ao selecionar um elemento, o Inspector registra somente os sinais disponíveis:

- processo e identidade do executável;
- janela;
- `AutomationId`, `Name`, `ControlType`, `ClassName` e `FrameworkId`;
- cadeia parcial de ancestrais;
- rótulos e controles próximos;
- posição relativa no contêiner.

O reencontro usa uma estratégia híbrida:

1. restringir a busca ao processo e à janela configurados;
2. tentar uma correspondência forte, como `AutomationId` e `ControlType`;
3. se necessário, pontuar candidatos pelos sinais restantes;
4. exigir pontuação mínima e margem suficiente entre os dois melhores candidatos;
5. retornar `Found`, `NotFound` ou `Ambiguous` com confiança e evidências.

Pesos, limiares e regras de desempate serão centralizados, versionados e testados. O motor não seleciona silenciosamente um candidato ambíguo. Mudanças de posição têm peso inferior a identificadores semânticos e estruturais.

## 7. Capture Engine e providers

O Capture Engine recebe um campo resolvido e tenta, na ordem configurada, providers compatíveis. Cada resultado contém valor, provider utilizado, confiança, duração e falhas intermediárias.

O primeiro provider usa Windows UI Automation e padrões como `ValuePattern`, `TextPattern` e `SelectionPattern`, conforme o tipo do controle. Futuras implementações poderão fornecer MSAA/Win32 e captura de região/OCR por meio do mesmo contrato.

O domínio recebe um valor normalizado ou uma falha tipada; ele não conhece objetos `AutomationElement` nem detalhes de OCR.

## 8. Inspector e modo de teste

No modo de seleção, o Agent acompanha o elemento sob o cursor, desenha um contorno que não intercepta o mouse e exibe seus atributos técnicos. Ao confirmar, gera o fingerprint e solicita ao usuário:

- significado semântico;
- tipo: campo ou gatilho;
- etapa associada;
- ações do gatilho, quando aplicável.

O próprio Agent e seu overlay são excluídos da inspeção.

Nenhuma configuração pode ser ativada antes de passar pelo modo de teste. Para cada campo e gatilho, o teste mostra:

- encontrado, ausente ou ambíguo;
- valor capturado quando autorizado;
- provider e fallback;
- confiança e sinais correspondentes;
- duração;
- avisos de fragilidade;
- transições e eventos em tempo real.

Mudanças posteriores podem colocar a integração em estado `Degraded` ou `Broken`, sempre com motivo visível.

## 9. Sessões, etapas e eventos

Cada instância monitorada do processo possui uma sessão independente, com etapa atual, valores temporários e itens confirmados. Um gatilho só é considerado quando válido para a etapa corrente.

O fluxo é:

```text
UI Automation
    -> Trigger detectado
    -> Seletores resolvidos
    -> Valores capturados
    -> Normalização e validação
    -> Ações e transição de etapa
    -> Domain Event
    -> Fila local
```

Cada evento contém ID único, ID e versão da configuração, ID da sessão, número sequencial, horário, tipo e payload. Essa estrutura permite reconstruir o atendimento e enviar eventos futuramente com idempotência.

O fluxo mínimo suporta etapas lineares, múltiplos botões por ação, `item_added`, `budget_finished`, retorno de etapa, cancelamento e limpeza do estado.

## 10. Persistência, privacidade e retenção

Configurações ficam em JSON versionado e não contêm valores de receitas. Eventos ficam em uma fila SQLite. O payload de negócio é protegido em repouso com DPAPI vinculado ao usuário Windows que executa o Agent.

Logs técnicos são separados dos dados de negócio e não registram valores capturados. Resultados exibidos durante inspeção permanecem em memória; um teste só persiste dados de negócio quando o usuário decidir salvá-lo.

Eventos confirmados pelo futuro backend serão retidos por sete dias para diagnóstico. Eventos ainda não confirmados permanecem até a confirmação, sujeitos a limite de armazenamento e alerta visível. A interface oferecerá limpeza explícita dos dados locais. A política será aplicada desde o primeiro milestone aos eventos do TestTarget.

## 11. Falhas e diagnóstico

Falhas são tipadas, pelo menos, como:

- aplicação fechada;
- janela ausente;
- elemento temporariamente indisponível;
- seletor inválido;
- seletor ambíguo;
- valor ilegível;
- provider incompatível ou com falha;
- fallback bem-sucedido;
- configuração degradada ou quebrada.

Falhas transitórias podem ser repetidas com limites e cancelamento. Ambiguidade e seletor inválido exigem ajuste ou novo teste. Nenhum evento válido é emitido se faltar um campo obrigatório. O Agent nunca falha silenciosamente.

## 12. TestTarget

`Prescriva.Agent.TestTarget` simula um ERP com:

- campos de texto;
- combo box;
- tabela de itens;
- botões `Avançar`, `Voltar`, `Adicionar`, `Finalizar` e `Cancelar`;
- campos habilitados e desabilitados;
- elementos que aparecem dinamicamente;
- pequenas variações controladas de layout.

Ele permite validar o Inspector, a resiliência dos seletores, leitura de valores, etapas, gatilhos e eventos sem depender de um ERP real.

## 13. Estratégia de testes

- testes unitários para domínio, scoring, ambiguidade, sessões, etapas, ações e eventos;
- testes de integração para JSON, migração de schema, SQLite, DPAPI e coordenação;
- testes Windows contra o TestTarget usando UI Automation real;
- roteiro manual para overlay, cursor, tray e fluxo completo.

Testes que dependem da área de trabalho interativa serão identificados separadamente para não serem confundidos com a suíte unitária. O roteiro manual registrará pré-condições e resultados esperados.

## 14. Critério de conclusão do milestone

O milestone estará concluído quando, em Windows 10 ou 11 x64, for possível:

1. iniciar o Agent e detectar o TestTarget;
2. selecionar visualmente campos e botões;
3. atribuir significados, etapas e ações;
4. salvar e recarregar a configuração;
5. reencontrar os elementos com diagnóstico;
6. detectar `Adicionar` e capturar os campos associados;
7. gerar e persistir `item_added`;
8. detectar `Finalizar` e gerar `budget_finished`;
9. mostrar valores, eventos, confiança e falhas no modo de teste;
10. passar pelo build, testes automatizados aplicáveis e roteiro manual documentado.

## 15. Riscos conhecidos

- ERPs podem não expor conteúdo suficiente pelo UI Automation; providers adicionais e OCR ampliarão a cobertura, mas não garantem captura universal.
- eventos de acessibilidade podem ser inconsistentes entre frameworks; gatilhos precisarão de estratégias alternativas e diagnóstico.
- mudanças extensas de layout podem tornar um seletor ambíguo; o Agent deve pedir reconfiguração em vez de adivinhar.
- automação entre processos exige cuidado com threading, ciclo de vida e desempenho.
- DPAPI vinculada ao usuário exige estratégia de migração se o modelo de execução mudar para serviço Windows.

## 16. Próximos documentos

Após a aprovação desta especificação:

1. registrar as decisões arquiteturais em ADRs;
2. criar o plano detalhado do primeiro milestone;
3. preparar `AGENTS.md`, documentação de arquitetura e handoff como tarefas do plano;
4. executar o plano com subagents, TDD, revisão e verificação conforme o workflow Superpowers.
