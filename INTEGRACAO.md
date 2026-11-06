# Integração no painel GPN — passo a passo

O chat de relatórios entra no painel como **duas pastas** (uma no back, uma no front) e **poucas linhas** de
código. Nenhum arquivo do painel é alterado além das linhas indicadas abaixo. O módulo não cria tabelas,
não roda migrations e só **lê** o banco.

| Parte | Copiar | Para |
|---|---|---|
| Back-end | `backend/AssistenteRelatorios/` (sem `bin/` e `obj/`) | ao lado do projeto da API do painel, na mesma solução |
| Front-end | `frontend/src/app/chat-relatorio/` | dentro de `src/app/` do Angular do painel |

O resto do repositório (`backend/Program.cs`, `backend/RelatorioIA.Api.csproj`, a casca do `frontend/` e `tests/`)
é só para demonstração e testes: **não vai para o painel**.

---

## 1. Back-end (API ASP.NET Core do painel)

**1.1 Referenciar o projeto do módulo** (os pacotes NuGet e os prompts vão junto automaticamente):

```bash
dotnet sln add AssistenteRelatorios/AssistenteRelatorios.csproj            # se usarem .sln
dotnet add <ApiDoPainel>.csproj reference AssistenteRelatorios/AssistenteRelatorios.csproj
```

**1.2 Duas linhas no `Program.cs` da API:**

```csharp
using RelatorioIA.Api;

builder.Services.AddAssistenteRelatorios(builder.Configuration);   // antes do builder.Build()
var app = builder.Build();
await app.InicializarAssistenteRelatoriosAsync();                   // logo depois do builder.Build()
```

Os controllers do módulo são achados pelo `AddControllers()` / `MapControllers()` que a API já tem.

**1.3 Configuração** (no `appsettings.json` da API ou, melhor, no cofre de segredos / variáveis de ambiente).
Modelo completo em `backend/appsettings.example.json`:

```jsonc
"ConnectionStrings": {
  "Tcudb": "Server=...;Database=Tcudb;User Id=<usuario-somente-leitura>;Password=...;TrustServerCertificate=True;Encrypt=True"
},
"Gemini": {
  "ApiKey": "<chave>",                     // ou variável de ambiente GEMINI_API_KEY
  "Model": "gemini-3.1-flash-lite",
  "ModelosReserva": [ "gemini-3.5-flash-lite", "gemini-flash-lite-latest" ],
  "TimeoutSegundos": 60,
  "SegundaChamadaAposSegundos": 25
},
"Relatorio": { "Pasta": "relatorios", "Consultoria": "GPN Consultoria", "Responsavel": "", "Contato": "" }
```

Se o painel já tem a string de conexão do Tcudb com outro nome, basta repetir com o nome `Tcudb`
(de preferência com um usuário **somente leitura**).

**1.4 Login:** colocar `[Authorize]` nos 3 controllers de `AssistenteRelatorios/Controllers/ChatController.cs`
(mesmo esquema do painel). Hoje estão abertos.

**Conferir antes:**
- Rotas usadas pelo módulo: `api/chat`, `api/relatorios`, `api/dados/catalogo` — não podem existir no painel.
- Versão: o módulo é .NET 9 (funciona em API .NET 9 ou 10). Se a API usar outra versão 9.x do EF Core,
  alinhe a versão do `Microsoft.EntityFrameworkCore.SqlServer` em `AssistenteRelatorios.csproj`.
- Servidor Linux: descomentar `SkiaSharp.NativeAssets.Linux` em `AssistenteRelatorios.csproj`.
- Internet de saída do servidor: `generativelanguage.googleapis.com` (Gemini) e, só na 1ª vez de cada município,
  `query.wikidata.org` / `commons.wikimedia.org` (brasão).
- A pasta `relatorios/` (arquivos gerados, apagados após 30 dias) e `brasoes/` são criadas na raiz do site;
  o usuário do IIS precisa de permissão de escrita.

## 2. Front-end (Angular do painel)

**2.1 Endereço da API** — uma linha no `app.config.ts` do painel:

```ts
import { CHAT_RELATORIO_API_URL } from './chat-relatorio';
import { environment } from '../environments/environment';

providers: [
  // ...providers que o painel já tem (provideHttpClient etc.)
  { provide: CHAT_RELATORIO_API_URL, useValue: environment.baseUrl },
]
```

As chamadas usam o `HttpClient` do painel, então o **interceptor de token** do painel é aplicado normalmente.

**2.2 Colocar o chat na tela nova** — escolha uma das formas:

**a) Dentro da tela que vocês estão criando** (o componente da tela importa o chat):

```ts
import { ChatRelatorioComponent } from '../chat-relatorio';   // ajuste o caminho relativo

@Component({
  // ...
  imports: [ChatRelatorioComponent /*, ...o que a tela já importa */],
})
```

```html
<!-- onde o chat deve aparecer -->
<app-chat-relatorio [municipio]="municipioSelecionado" />
```

**b) Como uma rota própria** (tela pronta, chat na página inteira):

```ts
{ path: 'relatorios-ia', loadComponent: () => import('./chat-relatorio').then(m => m.PaginaRelatoriosIaComponent) }
```

Sobre o `[municipio]`:
- aceita o nome ("Águas da Prata") ou o código ("aguas-da-prata");
- **com** município: o chat usa o da tela e não mostra seletor;
- **sem** município: o chat mostra um seletor com as cidades que existem no banco.

Altura do chat: `app-chat-relatorio { --chat-altura: 640px; }` (padrão 640px). Em grid de 2 colunas,
use `grid-column: 1 / -1` para ocupar a linha inteira. A fonte é herdada da página.

## 3. Testar depois de integrar

1. Subir a API do painel e abrir `GET api/dados/catalogo` → deve listar municípios e anos.
2. Abrir a tela, pedir "Quais os 5 maiores credores da saúde em 2025?" → deve gerar Word e PDF para baixar.
3. No log da API aparece "Tempo total ..." com o tempo de cada etapa.

Esse roteiro foi validado num projeto de API novo (`dotnet new webapi`), separado deste repositório:
pasta copiada, referência adicionada, 2 linhas no `Program.cs`, `dotnet publish` e pedido real ao chat
gerando Word e PDF.
