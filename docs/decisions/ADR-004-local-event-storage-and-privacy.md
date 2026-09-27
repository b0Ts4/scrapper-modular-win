# ADR-004: Fila SQLite com payload protegido por DPAPI

- **Status:** Aceita
- **Data:** 2026-09-27

## Contexto

Eventos precisam sobreviver a reinicializações e períodos offline. Os payloads podem conter dados pessoais e de saúde. Logs técnicos não devem duplicar esses valores.

## Decisão

Armazenar configurações sem dados de negócio em JSON versionado. Persistir eventos numa fila SQLite e proteger o payload com DPAPI no escopo do usuário Windows. Separar logs técnicos de dados de negócio. Reter eventos confirmados por sete dias; manter pendentes até confirmação, com limite e alerta.

## Consequências

A fila futura poderá enviar eventos com idempotência. O banco só poderá ser descriptografado pelo mesmo contexto de usuário. Uma futura migração para serviço Windows exigirá uma estratégia de chaves e migração de dados.

