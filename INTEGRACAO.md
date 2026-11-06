# Como colocar o chat no painel GPN

O chat é um **sistema à parte**: a API do chat roda como um site próprio, ao lado do painel, e a tela entra
no módulo do painel. **Nenhum arquivo do painel precisa ser alterado**, só a rota da tela nova.

```
backend/                     → copiar a pasta inteira: é a API do chat (publicar como site próprio)
frontend/chat-relatorio/     → copiar a pasta inteira para dentro do módulo do painel: é a tela do chat
```

---

## 1. BACK — publicar a API do chat

1. Copiar a pasta `backend/` inteira para o servidor/repositório (ex.: `chat-relatorio-api/`).
2. Criar o `appsettings.json` a partir do `appsettings.example.json` e preencher:
   - `ConnectionStrings:Tcudb` — banco do painel (usuário **somente leitura**);
   - `Gemini:ApiKey` — chave do Gemini;
   - `Cors:Origens` — endereço do painel, para o navegador poder chamar a API
     (ex.: `[ "https://painelgpnconsultoria.com" ]`).
3. Publicar e subir como um site próprio no IIS (ex.: `relatorios-api.painelgpnconsultoria.com`):
   ```bash
   dotnet publish RelatorioIA.Api.csproj -c Release -o <pasta-do-site>
   ```
4. Testar: abrir `https://<endereço-da-api>/api/dados/catalogo` → deve listar municípios e anos.

Requisitos do servidor: .NET 9 (ASP.NET Core Hosting Bundle), escrita nas pastas `relatorios/` e `brasoes/` do site,
e internet de saída para `generativelanguage.googleapis.com` (Gemini) e `query.wikidata.org` /
`commons.wikimedia.org` (brasão, só na 1ª vez de cada município). O módulo só **lê** o banco: não cria tabelas
nem roda migrations.

## 2. FRONT — a tela do chat no módulo do painel

1. Copiar a pasta `frontend/chat-relatorio/` inteira para dentro do módulo onde a tela vai ficar
   (ex.: `src/app/<modulo>/chat-relatorio/`).
2. Abrir `chat-relatorio/chat-relatorio.config.ts` e colocar o endereço da API do passo 1:
   ```ts
   export const CHAT_RELATORIO_CONFIG = {
     apiUrl: 'https://relatorios-api.painelgpnconsultoria.com/',
   };
   ```
3. Usar a tela `pagina-relatorios-ia.component.ts` (ela já usa os outros arquivos da pasta):

   **Como rota do módulo** (tela própria "Relatórios com IA"):
   ```ts
   import { PaginaRelatoriosIaComponent } from './chat-relatorio/pagina-relatorios-ia.component';

   { path: 'relatorios-ia', component: PaginaRelatoriosIaComponent }
   ```

   **Ou dentro de uma tela que já existe** (importar `PaginaRelatoriosIaComponent` no `imports` do componente):
   ```html
   <app-pagina-relatorios-ia />                              <!-- com seletor de municípios -->
   <app-pagina-relatorios-ia municipio="aguas-da-prata" />   <!-- já abre no município -->
   ```

A tela só precisa do `provideHttpClient()`, que o painel já tem.

## 3. Antes de liberar para os clientes

- **Login:** a API do chat ainda está aberta. Antes de liberar, ativar a validação do token do painel na API
  do chat (`[Authorize]` nos controllers de `AssistenteRelatorios/Controllers/ChatController.cs` + a mesma
  configuração JWT do painel no `Program.cs` da API do chat).
- **Faturamento do Gemini** ativo no projeto Google.

---

### Alternativa: embutir na API do painel (em vez de site próprio)

Se preferirem não ter um site a mais: copiar só `backend/AssistenteRelatorios/` para a solução da API do
painel, referenciar `AssistenteRelatorios.csproj` e acrescentar no `Program.cs` do painel
`builder.Services.AddAssistenteRelatorios(builder.Configuration);` (antes do `Build()`) e
`await app.InicializarAssistenteRelatoriosAsync();` (depois do `Build()`). Nesse caso, no
`chat-relatorio.config.ts` use o mesmo endereço da API do painel. Rotas usadas: `api/chat`, `api/relatorios`,
`api/dados/catalogo`.
