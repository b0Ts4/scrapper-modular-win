# Política de Privacidade — Prescriva Agent

**Última atualização:** 6 de outubro de 2026

O Prescriva Agent ("Agent") é um aplicativo para Windows que lê, de forma visível e somente nos campos que a pessoa usuária configurou, dados de orçamentos exibidos em outro programa do mesmo computador (por exemplo, o sistema de uma farmácia), e os registra localmente como eventos.

## 1. Quais dados o Agent coleta

- **Somente os campos configurados.** O Agent só lê elementos de tela que a pessoa usuária selecionou e confirmou no modo de configuração, e somente quando um botão configurado (por exemplo, "Adicionar" ou "Finalizar") é acionado. Ele não tem registrador de teclas, não lê a área de transferência, não monitora de forma oculta e não captura a tela inteira.
- **Valores dos campos:** por exemplo, nome do medicamento, concentração, quantidade e observações.
- **Arquivos e imagens (opcional):** quando um campo é configurado como "Arquivo / imagem", o Agent guarda uma cópia do arquivo indicado no campo (até 10 MB) ou a imagem exibida apenas no retângulo daquele controle. A captura de imagem é recusada se outra janela estiver cobrindo o controle.
- **Texto por OCR (opcional):** quando um campo é configurado como "Texto via OCR", o Agent reconhece o texto da imagem daquele controle usando o reconhecimento de texto do próprio Windows, **no computador**. A imagem é descartada após o reconhecimento.
- **Dados técnicos:** um registro técnico com identificadores, códigos de resultado, tempos e níveis de confiança. **Valores capturados nunca são gravados no registro técnico.**

O Agent não coleta dados de identificação da pessoa usuária, localização, contatos nem dados de navegação.

## 2. Onde os dados ficam e como são protegidos

- Todos os dados ficam **somente no computador**, na pasta de dados local do aplicativo. **Esta versão do Agent não envia dados a nenhum servidor**, nem à Microsoft, nem a terceiros.
- Os valores capturados e os anexos são **criptografados em repouso** com a proteção de dados do Windows (DPAPI), vinculada à conta do Windows que executa o Agent.
- As configurações guardam apenas como encontrar os campos na tela (seletores) e o que fazer com eles — **nunca** valores capturados.

## 3. Por quanto tempo os dados são mantidos

- Eventos ainda não confirmados permanecem até a confirmação. O Agent mostra um alerta visível quando a fila cresce.
- Eventos confirmados são mantidos por 7 dias para diagnóstico e depois apagados.
- Anexos que não são mais usados por nenhum evento são apagados automaticamente.

## 4. Controle da pessoa usuária

- **Visível:** o Agent mostra quando está monitorando, na janela e no ícone da bandeja.
- **Opt-in:** "Iniciar com o Windows" vem desativado.
- **Limpeza:** o botão "Limpar dados locais" apaga todos os eventos, anexos e o registro técnico. Desinstalar o aplicativo remove todos os dados locais.
- **Teste antes de ativar:** nenhuma configuração é ativada sem passar pelo modo de teste e ser aprovada.

## 5. Compartilhamento

O Agent não vende, aluga nem compartilha dados. Em versões futuras com envio ao servidor da organização usuária, esta política será atualizada **antes** dessa funcionalidade ser disponibilizada, descrevendo o destino, a finalidade e a forma de proteção.

## 6. Contato

Dúvidas sobre esta política: **barbosarthur98@gmail.com**.

---

# Privacy Policy — Prescriva Agent (English)

**Last updated:** October 6, 2026

Prescriva Agent is a Windows app that reads, visibly and only from the fields the user configured, budget data shown in another program on the same computer, and records it locally as events.

- **Collected:** only the values of configured fields, read only when a configured button is pressed; optionally a copy of a file named in a field (up to 10 MB), the image shown in that control (never when another window covers it), or text recognized from that control's image by the on-device Windows OCR (the image is discarded). A technical log keeps IDs, codes and timings — never captured values. No keylogging, clipboard reading, hidden monitoring or full-screen capture.
- **Storage:** on the computer only, encrypted at rest with Windows DPAPI for the signed-in user. **This version sends no data anywhere.** Configurations never contain captured values.
- **Retention:** unconfirmed events until confirmed (with a visible alert); confirmed events 7 days; unused attachments are deleted.
- **Control:** visible monitoring state; start with Windows is opt-in; "Clear local data" deletes everything; uninstalling removes all local data.
- **Sharing:** none. This policy will be updated before any future server upload feature ships.
- **Contact:** **barbosarthur98@gmail.com**.
