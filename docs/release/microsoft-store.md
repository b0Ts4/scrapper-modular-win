# Publicar o Prescriva Agent na Microsoft Store

A Store assina o pacote com o certificado da Microsoft, hospeda e atualiza automaticamente. Não é preciso comprar certificado. Publicador: pessoa física (decisão de 2026-10-06).

## 1. Conta e reserva do nome (uma vez — feito pelo publicador)

1. Crie a conta em <https://storedeveloper.microsoft.com> como **pessoa física**: conta Microsoft, documento e selfie. O registro é gratuito.
2. No Partner Center: **Apps e jogos → Novo produto → Aplicativo MSIX ou PWA**, e reserve o nome **Prescriva Agent** (ou outro disponível).
3. ✅ Feito (2026-10-06): nome reservado **Receita Fácil Agent**. Os valores de **Gerenciamento de produto → Identidade do produto** estão na seção 2:
   - `Package/Identity/Name` (ex.: `12345NomeSobrenome.PrescrivaAgent`)
   - `Package/Identity/Publisher` (ex.: `CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX`)
   - `Package/Properties/PublisherDisplayName` (o seu nome)

Esses valores entram no pacote: um pacote com outra identidade é recusado no envio.

## 2. Gerar o pacote

A identidade reservada está em `packaging/store-identity.json`:

| Campo | Valor |
| --- | --- |
| Nome do pacote | `ReceitaFcil.ReceitaFcilAgent` |
| Publicador | `CN=909D7733-A2AB-48F8-8EEE-74F749CF1C4F` |
| Nome do publicador | Receita Fácil |
| Nome de exibição | Receita Fácil Agent |

Há duas formas de obter o pacote:

- **Pelo CI (sem instalar nada):** cada execução do CI gera o pacote da Store no artefato **`msix-store`**. Abra a execução mais recente do branch `master` em *Actions*, baixe `msix-store`, descompacte o zip e use o `.msix`.
- **Localmente**, num Windows com o .NET SDK 10 e o Windows SDK:

  ```powershell
  ./packaging/build-msix.ps1 -Store -Version 1.0.0.0
  ```

**Não assine** o pacote para a Store: a Store assina. A versão deve terminar em `.0`, e cada envio precisa de uma versão maior que a anterior; o pacote do CI usa `1.0.<número da execução>.0`, que sempre cresce.

## 3. Envio (submission)

No Partner Center, em **Envio 1**:

- **Preços e disponibilidade:**
  - Gratuito.
  - Para distribuir só aos seus clientes, em **Visibilidade** escolha *Oculto na Store — somente quem tiver o link direto pode instalar* (ou *Público privado*, com uma lista de contas).
- **Propriedades:**
  - Categoria *Negócios* (ou *Produtividade*).
  - **URL da política de privacidade**: obrigatória. Pode ser o endereço público de `docs/privacy-policy.md` no GitHub, ou uma página do seu site com o mesmo texto (o e-mail de contato já está preenchido).
- **Classificação etária:** responda o questionário (sem conteúdo sensível, sem compras, sem chat).
- **Pacotes:** envie o `.msix` gerado no passo 2.
- **Listagem na Store (pt-BR):** use o texto da seção 5.
- **Declarações de capacidade:** o pacote usa `runFullTrust`, a capacidade restrita de apps de desktop. Na justificativa, informe:

  > Aplicativo de desktop (WPF/.NET) que lê, por UI Automation, os campos que a pessoa usuária configurou em outro programa do mesmo computador; guarda os dados localmente, criptografados com DPAPI.

A certificação costuma levar de algumas horas a poucos dias.

## 4. O que muda quando instalado pela Store

- **Iniciar com o Windows** usa a *tarefa de inicialização* do pacote, e não a chave `Run`. A pessoa também vê e controla a opção em **Gerenciador de Tarefas → Aplicativos de inicialização**; se ela desativar lá, o Agent mostra como reativar.
- **Dados locais** ficam na pasta privada do pacote e são apagados ao desinstalar.
- **Atualizações** chegam pela Store automaticamente: basta enviar um novo pacote com versão maior.

## 5. Texto da listagem (pt-BR)

**Nome:** Prescriva Agent

**Descrição curta:** Capture orçamentos do sistema da farmácia, de forma visível e só nos campos que você escolher.

**Descrição:**
O Prescriva Agent lê os dados de orçamento exibidos no sistema da sua farmácia (medicamento, concentração, quantidade, receita) sem integração com o fornecedor do sistema. Você aponta os campos e os botões na tela, testa e aprova; a partir daí, ao clicar em "Adicionar" ou "Finalizar" no seu sistema, o Agent registra o item ou o orçamento.

- Só lê os campos que você configurou, e só quando o botão configurado é acionado.
- Mostra quando está monitorando; nada é oculto.
- Guarda os dados apenas neste computador, criptografados.
- Lê também arquivos de receita e texto em imagens (OCR do próprio Windows).
- Pode iniciar com o Windows e voltar a monitorar sozinho.

**Recursos:** seleção visual de campos; modo de teste com evidências; eventos locais criptografados; arquivos e imagens; OCR; iniciar com o Windows.

## 6. Teste local de um pacote (sideload)

O CI gera e instala um pacote assinado com um certificado de teste (artefato `msix-package`). Para instalá-lo num PC de teste:

1. Importe o certificado público de teste em *Pessoas Confiáveis* (Máquina Local).
2. Dê dois cliques no `.msix`.

Pacotes de teste usam a identidade `PrescrivaAgent.Dev`; o pacote da Store, a identidade reservada.
