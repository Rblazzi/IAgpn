# Assistente de Relatórios — módulo para o painel GPN

Chat em que o usuário pede um relatório em linguagem natural ("relatório gerencial de janeiro a maio de 2026
comparando com 2025"). A IA (Gemini) interpreta o pedido e escreve as consultas, o sistema calcula os números
no banco do painel (Tcudb) e gera o relatório em **Word e PDF** no padrão da consultoria.

Stack igual à do painel: **ASP.NET Core (.NET 9)** + **Angular 19**.

```
chat-relatorio/
├── backend/
│   ├── AssistenteRelatorios/        ← O MÓDULO (copiar esta pasta inteira)
│   │   ├── AssistenteRelatoriosSetup.cs   registro: AddAssistenteRelatorios / InicializarAssistenteRelatoriosAsync
│   │   ├── Controllers/  Data/  Models/  Prompts/  Services/
│   ├── Program.cs                   host de demonstração (16 linhas)
│   ├── appsettings.json             configuração (contém senha e chave — NÃO versionar)
│   └── RelatorioIA.Api.csproj
├── frontend/
│   └── src/app/chat-relatorio/      ← O MÓDULO (copiar esta pasta: componente + serviço)
│       (o resto do frontend é só a casca de demonstração)
└── tests/                           72 testes de integração contra o Tcudb
```

## Integração no painel

### Back-end (API do painel)

1. Copiar `backend/AssistenteRelatorios/` para a raiz do projeto da API.
2. Adicionar os pacotes NuGet:
   ```xml
   <PackageReference Include="DocumentFormat.OpenXml" Version="3.3.0" />
   <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="9.0.10" />
   <PackageReference Include="PDFsharp-MigraDoc" Version="6.2.2" />
   <PackageReference Include="SkiaSharp" Version="3.119.0" />
   <!-- em servidor Linux: <PackageReference Include="SkiaSharp.NativeAssets.Linux" Version="3.119.0" /> -->
   ```
   e copiar os prompts na publicação:
   ```xml
   <None Update="AssistenteRelatorios\Prompts\**\*.md" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
   ```
   Se a API do painel usar outra versão do EF Core, alinhe a do `Microsoft.EntityFrameworkCore.SqlServer` com ela.
3. No `Program.cs` do painel, duas linhas (`using RelatorioIA.Api;`):
   ```csharp
   builder.Services.AddAssistenteRelatorios(builder.Configuration);   // antes do builder.Build()
   await app.InicializarAssistenteRelatoriosAsync();                   // depois do builder.Build()
   ```
   Os controllers são achados pelo `AddControllers()` / `MapControllers()` que o painel já tem.
4. Configuração (`appsettings` ou cofre de segredos do painel). No repositório só existe o modelo
   `backend/appsettings.example.json` (sem senha nem chave): copie para `backend/appsettings.json` e preencha.
   O `appsettings.json` fica fora do Git.
   ```jsonc
   "ConnectionStrings": { "Tcudb": "Server=...;Database=Tcudb;User Id=<somente leitura>;Password=...;TrustServerCertificate=True;Encrypt=True" },
   "Gemini": { "ApiKey": "...", "Model": "gemini-3.1-flash-lite", "ModelosReserva": [ "gemini-3.5-flash-lite", "gemini-flash-lite-latest" ], "TimeoutSegundos": 60, "SegundaChamadaAposSegundos": 25 },
   "Relatorio": { "Pasta": "relatorios", "PastaBrasoes": "brasoes", "Consultoria": "GPN Consultoria", "Responsavel": "", "Contato": "", "DiasManter": 30 },
   "Chat": { "PedidosSimultaneos": 3, "TempoMaximoMinutos": 8 }   // opcional
   ```
   A chave do Gemini também pode vir da variável `GEMINI_API_KEY`. Sem chave, o módulo funciona em "modo local" (sem IA).
   `SegundaChamadaAposSegundos`: se o Gemini não responder nesse tempo, uma 2ª chamada igual é disparada em paralelo e
   vale a que chegar primeiro (0 desliga). O log registra o tempo de cada etapa de cada pedido ("Tempo total ...").
5. **Autenticação:** colocar `[Authorize]` nos 3 controllers de `AssistenteRelatorios/Controllers/ChatController.cs`,
   com o mesmo esquema do painel, e, se for o caso, restringir o município ao do usuário logado.

Os nomes foram escolhidos para não colidir com o painel: o DbContext do módulo é `RelatorioDbContext`
(somente leitura, mapeado nas views `dbo.vw_ReceitaCompleta` e `dbo.vw_DespesaCompleta`) e o namespace é `RelatorioIA.Api.*`.

**Rotas usadas:** `api/chat`, `api/relatorios` e `api/dados/catalogo`. Confira se o painel não tem rotas com esses nomes.

### Front-end (Angular do painel)

1. Copiar `frontend/src/app/chat-relatorio/` para `src/app/` do painel.
2. No fim da página do dashboard, como último card (já segue o visual dos cards do painel: fundo branco, título
   centralizado, menu ☰, verde do painel; a fonte é herdada da página):
   ```html
   <app-chat-relatorio [municipio]="municipioSelecionado" />
   ```
   `municipio` aceita o nome ("Águas da Prata") ou o código ("aguas-da-prata"). Com ele informado, o chat não mostra
   seletor e usa o município da página. Importe `ChatRelatorioComponent` no componente da página (standalone).
   Altura do card: `app-chat-relatorio { --chat-altura: 640px; }` (padrão 640px). Se a página usar grid de 2 colunas,
   faça o chat ocupar a linha inteira (`grid-column: 1 / -1`).
3. O serviço usa `environment.baseUrl + 'api/...'`, como os serviços do painel. O download usa `HttpClient`, então passa pelo
   interceptor de token do painel. Requer `provideHttpClient()`, que o painel já tem.

## Endpoints

| Método | Rota | Uso |
|---|---|---|
| POST | `api/chat/iniciar` | `{ mensagem, historico[], municipio? }` → `{ id }` (processa em segundo plano; usado pela tela) |
| GET | `api/chat/{id}` | `{ status, etapa, segundos, resultado?, erro? }` (a tela consulta a cada 1,5 s) |
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
10. **Sem IA** (Gemini fora do ar ou sem chave): o `AssistenteLocal` interpreta o pedido por regras (relatório gerencial,
   períodos e projeção com a consulta padrão do histórico) e usa textos e comentários padrão.

Os prompts (`AssistenteRelatorios/Prompts/*.md`) são lidos a cada pedido, então uma edição vale na hora.

## Rodar a demonstração

```bash
cd backend && dotnet run --launch-profile http        # API em http://localhost:5101 (aceita acesso pela rede)
cd frontend && npm install && npm start               # tela em http://localhost:4200 (aceita acesso pela rede)
cd tests && dotnet test                               # 72 testes contra o Tcudb (conexão do backend/appsettings.json ou variável TCUDB)
```

**Rode `dotnet test` antes de publicar qualquer alteração.** Se um teste do gabarito quebrar, os relatórios deixaram de bater com o oficial.

## Pendências para produção

- [ ] Senha do Tcudb e chave do Gemini fora do `appsettings.json` (cofre ou variáveis de ambiente).
- [ ] `[Authorize]` nos controllers e município restrito ao usuário.
- [ ] Faturamento ativo no projeto Google (o plano gratuito tem muitos erros 503 e pode usar os dados).
- [ ] Pedidos em segundo plano ficam em memória (1 instância). Para várias instâncias, usar um store compartilhado (Redis ou tabela).
- [x] Brasão do município na capa (Wikidata). [ ] Imagem da assinatura na última página (aguardando os arquivos).
