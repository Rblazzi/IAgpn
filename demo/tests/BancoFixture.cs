using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using RelatorioIA.Api.Data;
using RelatorioIA.Api.Services;

namespace RelatorioIA.Tests;

/// <summary>
/// Testes de INTEGRAÇÃO contra o banco real do painel (Tcudb, SQL Server, usuário somente leitura).
/// A connection string vem de ConnectionStrings:Tcudb no demo/api/appsettings.json (ou da variável TCUDB).
/// Gabarito: "Águas da Prata Relatório Gerencial - 26-08-2026.pdf" (dados de 2025 e 2026 do TCESP).
/// </summary>
public sealed class BancoFixture
{
    public const string Municipio = "Águas da Prata";
    public const string Codigo = "aguas-da-prata";

    public string ConnectionString { get; } = LerConexao();
    public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());

    public DbContextOptions<RelatorioDbContext> Opcoes => new DbContextOptionsBuilder<RelatorioDbContext>().UseSqlServer(ConnectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).Options;

    public RelatorioDbContext NovoContexto() => new(Opcoes);

    public IConfiguration Config => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Tcudb"] = ConnectionString })
        .Build();

    /// <summary>A calculadora abre os próprios contextos (consultas em paralelo); o parâmetro fica por compatibilidade.</summary>
    public CalculadoraRelatorio Calculadora(RelatorioDbContext? _ = null) => new(Opcoes, Cache, Config);

    static string LerConexao()
    {
        if (Environment.GetEnvironmentVariable("TCUDB") is { Length: > 0 } env) return env;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "demo", "api", "appsettings.json"))) d = d.Parent;
        if (d == null) throw new InvalidOperationException("demo/api/appsettings.json não encontrado");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(d.FullName, "demo", "api", "appsettings.json")));
        return json.RootElement.GetProperty("ConnectionStrings").GetProperty("Tcudb").GetString()
               ?? throw new InvalidOperationException("ConnectionStrings:Tcudb não configurada");
    }
}

[CollectionDefinition("banco")]
public class BancoCollection : ICollectionFixture<BancoFixture>;
