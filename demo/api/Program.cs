// Host de DEMONSTRAÇÃO do módulo. No sistema real, só as 2 linhas marcadas com ★ entram no Program.cs existente.
using RelatorioIA.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAssistenteRelatorios(builder.Configuration);   // ★
builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origens").Get<string[]>() ?? ["http://localhost:4200"])
    .AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Content-Disposition")));

var app = builder.Build();
await app.InicializarAssistenteRelatoriosAsync();                   // ★
app.UseCors();
// Só na demonstração: a tela (build do Angular copiado para wwwroot/) é servida no mesmo endereço da API
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();
