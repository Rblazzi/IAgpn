# Assistente de Relatórios — módulo para o painel GPN

Chat em que o usuário pede um relatório em linguagem natural ("relatório gerencial de janeiro a maio de 2026
comparando com 2025"). A IA (Gemini) interpreta o pedido e escreve as consultas, o sistema calcula os números
no banco do painel (Tcudb) e gera o relatório em **Word e PDF** no padrão da consultoria.

Stack igual à do painel: **ASP.NET Core (.NET 9)** + **Angular 19**.

```
chat-relatorio/
├── backend/                         ★ COPIAR INTEIRA: a API do chat (sistema à parte, site próprio)
│   ├── AssistenteRelatorios/          o código (IA, banco, Word/PDF, prompts)
│   ├── Program.cs, RelatorioIA.Api.csproj
│   └── appsettings.example.json       modelo da configuração (o appsettings.json real fica fora do Git)
├── frontend/
│   └── chat-relatorio/              ★ COPIAR INTEIRA para o módulo do painel: a tela do chat
│       ├── pagina-relatorios-ia.component.ts   a tela (usar como rota ou <app-pagina-relatorios-ia />)
│       └── chat-relatorio.config.ts            endereço da API do chat (único ajuste)
├── INTEGRACAO.md                    passo a passo
└── README.md
```
A pasta `demo/` (tela de teste e testes automáticos) fica só no computador de desenvolvimento, fora do Git.

## Integração no painel

Passo a passo em **[INTEGRACAO.md](INTEGRACAO.md)**. Resumo:

- **Back:** publicar a pasta `backend/` como um site próprio (configurar `appsettings.json` e `Cors:Origens`).
- **Front:** copiar `frontend/chat-relatorio/` para o módulo, colocar o endereço da API em
  `chat-relatorio.config.ts` e usar a tela `PaginaRelatoriosIaComponent` (rota ou tag).

## Endpoints

| Método | Rota | Uso |
|---|---|---|
| POST | `api/chat/iniciar` | `{ mensagem, historico[], municipio?, relatorioAnterior?, anexos[]? }` → `{ id }` (processa em segundo plano; usado pela tela) |
| POST | `api/anexos` | Arquivo (multipart, campo `arquivo`; PDF, DOCX, XLSX, CSV, TXT, PNG, JPG até 10 MB) → `{ id, nome, tamanho }` |
| GET | `api/chat/{id}` | `{ status, etapa, segundos, resultado?, erro? }` (a tela consulta a cada 1 s) |
| DELETE | `api/chat/{id}` | Cancela o pedido |
| POST | `api/chat` | Mesmo pedido, síncrono (integrações e testes; sujeito ao timeout do IIS) |
| GET | `api/relatorios/{id}?formato=docx\|pdf` | Download do relatório |
| GET | `api/dados/catalogo` | Municípios, anos e meses completos disponíveis |

## Como funciona

1. **Catálogo** (`CalculadoraRelatorio`): municípios, exercícios e o último mês **completo**, ou seja, o mês em que todos os órgãos
   (Prefeitura, Câmara, RPPS…) já enviaram ao TCESP. Fica em cache por 10 minutos.
2. **Plano** (prompt `01`): a IA decide entre relatório gerencial, relatório por tema, resposta curta ou pergunta de volta,
   e escreve os `SELECT` em T-SQL sobre `vw_despesas` / `vw_receitas`.
3. **SQL seguro** (`ExecutorSql`): `vw_despesas` e `vw_receitas` são CTEs montadas pelo sistema sobre as views do painel,
   já com as regras do TCESP e collation sem acento. O validador só aceita `SELECT` nessas duas fontes e bloqueia escrita,
   `dbo.`/`sys.`, `EXEC`, `WAITFOR`, `INTO`, `@`/`#`, comentários etc. Limites: 30 s e 500 linhas. O usuário do banco é somente leitura.
   Consulta com erro ou vazia volta para a IA corrigir (até 2 rodadas). O SQL fica só no log.
4. **Cálculo:** todos os números saem do banco, nunca da IA. Despesa empenhada consolidada = Empenhado + Reforço − Anulação.
   Receita inclui as deduções (FUNDEB). Conferido centavo a centavo com o relatório oficial de Águas da Prata (26/08/2026).
5. **Projeção** (`Projetor`): taxa mediana das variações mês a mês, cenários tendência e estável, e estudo estatístico
   (média, desvio padrão, margem de erro com 95% de confiança). Meses passados sem dados completos **não são estimados**.
6. **Redação** (prompts `02`/`03`/`04`): a IA escreve os textos (um comentário por quadro). As tabelas e os gráficos são preenchidos
   pelo sistema, e valores citados no texto que não estão nos resultados geram aviso.
7. **Documento** (`Services/Documentos`): Word (OpenXML) e PDF (MigraDoc) com o mesmo visual, e gráficos (SkiaSharp).
   A conclusão sempre traz que a análise é quantitativa e sugere reuniões com os gestores.
8. **Brasão na capa** (`Services/Documentos/Brasoes.cs`): vem do Wikidata/Wikimedia Commons (propriedade P94 dos
   municípios, ligados pelo código IBGE; 613 dos 645 municípios de SP têm). Na 1ª vez, o índice da UF e a imagem são
   baixados e guardados em `brasoes/` (`aguas-da-prata.png`); depois não vai mais à internet. Para corrigir um brasão,
   substitua o arquivo na pasta. Sem brasão ou sem internet, a capa sai como antes. Para ler do banco no futuro, basta
   implementar `IFonteBrasao` e trocar o registro no `AssistenteRelatoriosSetup`. O servidor precisa acessar
   `query.wikidata.org` e `commons.wikimedia.org` (só na 1ª vez de cada município).
9. **Assinatura da última página:** padrão `Relatorio:Responsavel` (ou a consultoria). No chat, o usuário pode pedir
   outra ("troque GPN Consultoria por Gestor responsável"): o sistema reabre o último relatório da conversa (salvo
   em JSON junto dos arquivos) e troca só essa linha, sem refazer consultas. A assinatura vale para os próximos
   relatórios da conversa. A tela envia `relatorioAnterior` (id do último relatório) no `api/chat/iniciar`.
10. **Histórico e continuidade:** a conversa fica salva no navegador (uma por município) e continua depois de recarregar a
   página ("Nova conversa" no menu limpa). Cada relatório grava um `.contexto` ao lado dos arquivos (pedido, consultas, seções,
   anexos e títulos); no pedido seguinte a tela envia `relatorioAnterior` e a IA recebe esse contexto.
   **O usuário vai ajustando o MESMO documento** (acao `editar_relatorio`): o sistema reabre o documento salvo (JSON) e
   - "acrescente/adicione X (no final)": a IA consulta e escreve **só o conteúdo novo**, que entra como um capítulo antes da
     Conclusão (com tabelas e gráficos), e um parágrafo sobre o tema entra na Conclusão;
   - "reescreva a conclusão", "no comentário de X destaque...", "mude a tabela de Y para...": altera **só aquela seção**, no
     mesmo lugar. Pedido de texto mantém as tabelas e gráficos da seção (e cada parágrafo na mesma posição, sem criar
     parágrafos novos); só "troque a tabela/os dados" refaz a tabela. O resto do documento fica idêntico;
   - "tire/remova a seção Y": remove a seção (até o próximo título do mesmo nível);
   - o arquivo mantém o mesmo nome; o restante do documento não é refeito.
   Um relatório novo só é gerado quando o usuário pede outro relatório ou muda a base (município, período do relatório).
11. **Anexos (📎)** (`Services/Anexos.cs`): o usuário anexa até 5 arquivos por pedido. Word, Excel, CSV e TXT viram texto;
   PDF e imagens vão inteiros para o Gemini ler. A extensão é conferida com o conteúdo do arquivo. A IA pode transcrever
   imagens anexadas (fotos/prints) no documento como estão (`imagemAnexo`: orientação do celular corrigida, até 1600 px, JPEG,
   legenda e "Fonte: imagem anexada") e transcrever tabelas do arquivo (`tabelaAnexo`), que recebem o mesmo padrão (coluna % e linha TOTAL), a nota "Fonte: arquivo anexado"
   e gráfico; cada valor transcrito é conferido com o arquivo e com o banco (o que não bate gera aviso). Os anexos ficam em
   `relatorios/anexos/` e são apagados junto com os relatórios antigos.
12. **Comparativo entre anos:** anos no cabeçalho, % de evolução entre anos seguidos e "TOTAL %"; com mais de 4 anos a
   tabela continua horizontal, em blocos de até 4 anos.
13. **Sem IA** (Gemini fora do ar ou sem chave): o `AssistenteLocal` interpreta o pedido por regras (relatório gerencial,
   períodos e projeção com a consulta padrão do histórico) e usa textos e comentários padrão.

**Recursos de uso:**
- **Documento ao lado do chat:** o PDF do último relatório aparece à direita e se atualiza a cada ajuste ("Visualizar ao
  lado" em cada versão; em telas estreitas abre em nova aba).
- **Excel:** cada relatório também gera um .xlsx com as tabelas (uma aba por tabela, blocos de meses/anos juntos numa aba),
  com R$ e % como números de verdade.
- **Cores nas tabelas:** colunas de variação/diferença em verde (alta) e vermelho (queda), no Word e no PDF.
- **Correção automática:** se o texto da IA citar um valor em R$ que não está nos dados, o sistema pede uma vez que ela
  reescreva com os valores corretos; se ainda sobrar algum, o aviso "Confira…" continua.

**Postura da IA** (`Prompts/00-identidade.md`, incluído em todos os prompts): especialista em consulta e validação dos dados
do TCESP; acata as solicitações do operador; nunca inventa números, nomes ou textos; tom profissional; pedidos fora do
escopo recebem a resposta de que o assistente é exclusivo para finanças públicas municipais; alterações mudam só o pedido.

Os prompts (`AssistenteRelatorios/Prompts/*.md`) são lidos a cada pedido, então uma edição vale na hora.

## Rodar a demonstração

```bash
# 1ª vez: copiar backend/appsettings.example.json para backend/appsettings.json e preencher
cd backend && dotnet run --launch-profile http       # API em http://localhost:5101 (aceita acesso pela rede)
cd demo/web && npm install && npm start              # tela de teste em http://localhost:4200
cd demo/tests && dotnet test                         # testes contra o Tcudb (conexão do backend/appsettings.json ou variável TCUDB)

# Tela compilada servida pela própria API (um endereço só, ex.: para o túnel):
cd demo/web && npm run build && cp -r dist/frontend/browser ../../backend/wwwroot
```

**Rode `dotnet test` antes de publicar qualquer alteração.** Se um teste do gabarito quebrar, os relatórios deixaram de bater com o oficial.

## Pendências para produção

- [ ] Senha do Tcudb e chave do Gemini fora do `appsettings.json` (cofre ou variáveis de ambiente).
- [ ] `[Authorize]` nos controllers e município restrito ao usuário.
- [ ] Faturamento ativo no projeto Google (o plano gratuito tem muitos erros 503 e pode usar os dados).
- [ ] Pedidos em segundo plano ficam em memória (1 instância). Para várias instâncias, usar um store compartilhado (Redis ou tabela).
- [x] Brasão do município na capa (Wikidata). [ ] Imagem da assinatura na última página (aguardando os arquivos).
