using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Data;
using RelatorioIA.Api.Services;
using RelatorioIA.Api.Services.Consultas;
using RelatorioIA.Api.Services.Documentos;
using RelatorioIA.Api.Services.IA;

namespace RelatorioIA.Api;

/// <summary>
/// Registro do módulo "Assistente de Relatórios". No Program.cs do sistema que recebe o módulo:
/// <code>
/// builder.Services.AddAssistenteRelatorios(builder.Configuration);
/// ...
/// var app = builder.Build();
/// await app.InicializarAssistenteRelatoriosAsync();
/// </code>
/// Configuração usada: ConnectionStrings:Tcudb, Gemini, Relatorio, Chat e (opcional) Municipios.
/// </summary>
public static class AssistenteRelatoriosSetup
{
    public static IServiceCollection AddAssistenteRelatorios(this IServiceCollection services, IConfiguration cfg)
    {
        services.Configure<GeminiOptions>(cfg.GetSection("Gemini"));
        services.Configure<RelatorioOptions>(cfg.GetSection("Relatorio"));

        // Banco do painel (SQL Server, usuário somente leitura nas views vw_ReceitaCompleta / vw_DespesaCompleta)
        var conexao = cfg.GetConnectionString("Tcudb");
        if (string.IsNullOrWhiteSpace(conexao))
            throw new InvalidOperationException("Configure ConnectionStrings:Tcudb (banco do painel).");
        services.AddDbContext<RelatorioDbContext>(o => o
            .UseSqlServer(conexao, s => s.CommandTimeout(60).EnableRetryOnFailure(3))
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        services.AddMemoryCache();

        // Dados, cálculo e SQL da IA
        services.AddScoped<CalculadoraRelatorio>();
        services.AddSingleton<EsquemaConsulta>();
        services.AddSingleton<ExecutorSql>();

        // Documento padrão → Word / PDF
        services.AddSingleton<MontadorGerencial>();
        services.AddSingleton<MontadorConsulta>();
        services.AddSingleton<RenderizadorDocx>();
        services.AddSingleton<RenderizadorPdf>();
        // Brasão da capa: Wikidata/Commons com cache em disco. Para ler do banco no futuro, trocar só esta linha.
        services.AddHttpClient<IFonteBrasao, BrasaoWikidata>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AssistenteRelatorios/1.0 (GPN Consultoria)");   // exigido pela Wikimedia
        });

        // IA: Gemini só é registrado se houver chave (Gemini:ApiKey ou variável GEMINI_API_KEY); sem chave = modo local
        var chave = cfg["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(chave)) chave = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(chave))
        {
            services.PostConfigure<GeminiOptions>(o => o.ApiKey = chave);
            services.AddHttpClient<IModeloIA, GeminiModelo>(c => c.Timeout = TimeSpan.FromSeconds(180));
        }
        services.AddSingleton<PromptStore>();
        services.AddSingleton<MunicipiosConhecidos>();
        services.AddSingleton<AssistenteLocal>();
        services.AddScoped<AssistenteRelatorios>();
        services.AddScoped<ChatService>();
        services.AddSingleton<Anexos>();
        services.AddSingleton<JobsChat>();
        return services;
    }

    /// <summary>Testa o acesso ao banco (aquecendo o catálogo) e apaga relatórios antigos (Relatorio:DiasManter, padrão 30).</summary>
    public static async Task InicializarAssistenteRelatoriosAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cat = await sp.GetRequiredService<CalculadoraRelatorio>().CatalogoAsync();
            app.Logger.LogInformation("Catálogo do banco carregado em {S:0.0}s", sw.Elapsed.TotalSeconds);
            // Em segundo plano: valores distintos (vão no prompt da IA), brasões e a lista de municípios da UF
            var nomes = cat.Select(c => c.Municipio).Distinct().ToList();
            _ = Task.Run(async () =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                try { await app.Services.GetRequiredService<EsquemaConsulta>().ValoresDistintosAsync(); }
                catch (Exception ex) { app.Logger.LogWarning(ex, "Não foi possível pré-carregar os valores distintos"); }
                app.Logger.LogInformation("Valores distintos pré-carregados em {S:0.0}s", t.Elapsed.TotalSeconds);
            });
            _ = Task.Run(async () =>
            {
                using var s = app.Services.CreateScope();
                var fonte = s.ServiceProvider.GetRequiredService<IFonteBrasao>();
                foreach (var n in nomes) await fonte.ObterAsync(n);
            });
            // Mantém o catálogo (10 min) e os valores distintos (30 min) sempre "quentes": são recalculados em segundo
            // plano antes de expirar, então nenhum pedido do chat espera por essas consultas pesadas
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMinutes(8));
                var voltas = 0;
                while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
                {
                    try
                    {
                        using var s = app.Services.CreateScope();
                        await s.ServiceProvider.GetRequiredService<CalculadoraRelatorio>().AtualizarCatalogoAsync(app.Lifetime.ApplicationStopping);
                        if (++voltas % 3 == 0)
                            await app.Services.GetRequiredService<EsquemaConsulta>().AtualizarAsync(app.Lifetime.ApplicationStopping);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { app.Logger.LogWarning(ex, "Falha ao atualizar o catálogo em segundo plano"); }
                }
            });
            foreach (var m in cat.GroupBy(c => c.Municipio))
                app.Logger.LogInformation("Tcudb: {Municipio} ({Codigo}) — {Anos} exercícios, {Primeiro} a {Ultimo}{Pend}", m.Key,
                    m.First().Codigo, m.Count(), m.Min(c => c.Ano), m.Max(c => c.Ano),
                    m.Last().Pendencias is { } p ? $" | pendências {m.Last().Ano}: {p}" : "");
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Assistente de Relatórios: não foi possível acessar o banco (ConnectionStrings:Tcudb).");
        }

        var rel = sp.GetRequiredService<IOptions<RelatorioOptions>>().Value;
        var pasta = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, rel.Pasta));
        if (Directory.Exists(pasta))
        {
            var dias = app.Configuration.GetValue("Relatorio:DiasManter", 30);
            // Relatórios, contextos e anexos (subpasta anexos/)
            foreach (var f in Directory.GetFiles(pasta, "*", SearchOption.AllDirectories).Where(f => File.GetCreationTimeUtc(f) < DateTime.UtcNow.AddDays(-dias)))
                try { File.Delete(f); } catch (IOException) { }
        }
        app.Logger.LogInformation("Assistente de Relatórios — motor de IA: {Motor}", sp.GetRequiredService<AssistenteRelatorios>().Nome);
    }
}
