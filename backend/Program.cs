// API do chat de relatórios — sistema à parte, publicado como um site próprio (ao lado do painel).
// Configuração em appsettings.json (modelo: appsettings.example.json):
//   ConnectionStrings:Tcudb, Gemini:ApiKey, Relatorio, e Cors:Origens = endereço do painel (ex.: https://painelgpnconsultoria.com).
using RelatorioIA.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAssistenteRelatorios(builder.Configuration);
builder.Services.AddControllers();
// O front do painel chama esta API de outro endereço: liberar a origem do painel em Cors:Origens
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origens").Get<string[]>() ?? ["http://localhost:4200"])
    .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition")));

var app = builder.Build();
await app.InicializarAssistenteRelatoriosAsync();
app.UseCors();
// Opcional: se existir wwwroot/ (tela compilada), ela é servida no mesmo endereço da API (usado nos testes externos)
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();
