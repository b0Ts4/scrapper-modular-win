# ADR-003: Seletores híbridos com confiança e ambiguidade explícita

- **Status:** Aceita
- **Data:** 2026-09-27

## Contexto

Identificadores de UI Automation não são uniformes. Coordenadas e caminhos absolutos quebram com pequenas mudanças, enquanto busca puramente aproximada pode selecionar o campo errado.

## Decisão

Persistir fingerprints compostos. Restringir candidatos por aplicação e janela, tentar correspondência forte e depois pontuar sinais semânticos, estruturais e posicionais. Exigir limiar mínimo e margem entre os dois melhores resultados. Retornar `Found`, `NotFound` ou `Ambiguous` com evidências.

## Consequências

O Agent tolerará mudanças pequenas sem adivinhar em situações incertas. Pesos e limiares passam a ser comportamento versionado, documentado e coberto por testes.

