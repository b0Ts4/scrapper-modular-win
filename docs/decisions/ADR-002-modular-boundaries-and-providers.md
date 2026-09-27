# ADR-002: Núcleo modular com providers substituíveis

- **Status:** Aceita
- **Data:** 2026-09-27

## Contexto

O Agent deve configurar novos ERPs sem adapters específicos. Métodos de captura variarão entre UI Automation, MSAA/Win32 e OCR.

## Decisão

Separar Domain, Application, Windows, Infrastructure e Desktop. O domínio não referencia Windows, WPF, persistência ou providers concretos. Captura, resolução de seletores, armazenamento e relógio são contratos consumidos pela camada Application. O primeiro provider será UI Automation.

## Consequências

Novos providers poderão ser adicionados sem alterar regras de sessão, gatilhos e eventos. A solução terá mais projetos e interfaces, compensados por testes isolados e dependências explícitas.

