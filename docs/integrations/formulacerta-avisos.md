# Avisos do FórmulaCerta no lançamento de orçamento — para decisão das farmacêuticas

**Para que serve:** o robô do Agent vai lançar orçamentos no FórmulaCerta como uma atendente. Durante o lançamento o sistema mostra avisos e perguntas. Para cada um, as farmacêuticas decidem o que o robô faz:

- **Responder sozinho:** dizer qual botão ele aperta.
- **Parar e chamar uma pessoa:** o robô deixa a tela exatamente como está.

**Regra fixa:** qualquer aviso que não esteja nesta lista faz o robô **parar**.

**Origem da lista:** são os avisos que vimos nos testes com dados simulados (18 a 20/10/2026). Ela vai crescer conforme testarmos mais casos. Os novos entram no fim, marcados como "novo".

Legenda da proposta: ✅ responder sozinho · ⛔ parar e chamar uma pessoa · ➖ não depende de decisão (é passo normal do lançamento).

## 1. Durante o preenchimento da fórmula

| # | Título da janela | O que diz | Quando aparece | Botões | Proposta | Decisão da farmacêutica |
|---|---|---|---|---|---|---|
| 1 | Funcionário Recepção | Pede o código do funcionário | Na primeira vez que o cursor entra na fórmula | OK / Cancelar | ➖ preencher o funcionário configurado e OK | |
| 2 | Atenção! | "Código do funcionário obrigatório" | Ao cancelar o item 1 sem código | OK | ⛔ parar (não deveria acontecer) | |
| 3 | Atenção! | "Produto abaixo do Estoque Mínimo!" | Ao lançar um componente com estoque baixo | OK | ✅ OK e continuar (só informa) | |
| 4 | Atenção! | "Quantidade zerada na ficha de pesagem! … A quantidade calculada para esta substância é 0,0000000 … Consulte o FARMACÊUTICO RESPONSÁVEL." | Quando o componente entra antes da quantidade de cápsulas, ou quando a dose calculada fica abaixo de 0,0001 | OK / Cancelar | ⛔ parar (pode ser dose errada) | |
| 5 | (mensagem de erro) | "'' is not a valid integer value" | Campo numérico deixado vazio | OK | ⛔ parar | |
| 6 | Funcionário Inativo | Funcionário marcado como inativo | Ao usar um funcionário inativo | OK | ⛔ parar | |

## 2. Depois do OK (gravação do orçamento)

| # | Título da janela | O que diz | Botões | Proposta | Decisão da farmacêutica |
|---|---|---|---|---|---|
| 7 | Componente | "Q. S. P. ?" | Sim / Não / Cancelar | ➖ responder conforme a fórmula conferida no CRM. Se o CRM não disser: ⛔ parar | |
| 8 | (confirmação) | "Dados Corretos?" | Sim / Não | ✅ Sim (os dados já foram conferidos no CRM) | |
| 9 | Embalagem | Escolha da embalagem | OK / Cancelar | ✅ aceitar a embalagem sugerida pelo sistema | |
| 10 | Condições de Pagamento | Forma de pagamento | OK / Cancelar | ✅ aceitar o padrão (o robô **nunca** aprova pagamento) | |
| 11 | Acréscimo/Desconto | Acréscimo ou desconto | OK / Cancelar | ✅ sem acréscimo e sem desconto (o robô **nunca** altera preço) | |
| 12 | Retirada | Data e hora de retirada | OK / Cancelar | ✅ aceitar a data e hora sugeridas | |
| 13 | Retirada de Fórmulas | Retirada quando há várias fórmulas | OK / Cancelar | ✅ aceitar o sugerido | |
| 14 | Orçamento \<número\> | "Mais Fórmula(s)?" | Sim / Não | ➖ Sim se houver outra fórmula no pedido, senão Não | |
| 15 | (impressão) | Janela de impressão | Imprimir / Cancelar | ✅ Cancelar (o robô **nunca** imprime) | |
| 16 | Orçamento \<número\> | "Deseja efetuar a inclusão do orçamento?" | Sim / Não | ➖ Sim (grava o orçamento) | |
| 17 | Requisição | Criar requisição (produção) | OK / Cancelar | ✅ Cancelar (o robô **nunca** cria requisição) | |

Os itens 15 a 17 aparecem quando o orçamento é feito pela tela de Receitas. Pela *Nova Fórmula* do pedido do MobyPharma, a tela fecha depois do último "Não" do item 14.

## 3. Outras janelas do caminho

| # | Título da janela | O que diz | Proposta | Decisão da farmacêutica |
|---|---|---|---|---|
| 18 | Requisição (ao abrir Novo Orç.) | Novo orçamento automático/manual, fonte | ➖ automático, sem fonte | |
| 19 | Atenção | "Dados não gravados. Confirma cancelamento?" | ➖ só quando o robô precisa desistir: Sim | |
| 20 | MobyPharma: atender pedido | Confirmação de "Atender" | ➖ Sim (pega o pedido) | |
| 21 | Cadastro do cliente | O sistema abre o cadastro do cliente | ⛔ parar (o robô **nunca** altera cadastro) | |

## 4. Casos em que o robô sempre para, independentemente de aviso

- produto não encontrado pelo código, ou com descrição diferente da esperada;
- substância controlada (SNGPC);
- qualquer valor que não confira depois de digitado;
- campo ou tela não encontrados;
- demora maior que o limite.

## 5. Perguntas às farmacêuticas

1. O item 3 (estoque mínimo) pode mesmo seguir sem ninguém ver?
2. No item 4, há casos em que a farmacêutica normalmente aceita e segue?
3. Nos itens 9 a 13, os valores sugeridos pelo sistema são sempre os certos para pedidos do WhatsApp/Loja Virtual?
4. Existe algum outro aviso que vocês veem com frequência e que não está aqui? Por exemplo: interação medicamentosa, dose máxima, incompatibilidade, produto inativo, vencido.
