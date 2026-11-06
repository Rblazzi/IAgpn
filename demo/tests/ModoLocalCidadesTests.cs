using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Services;
using RelatorioIA.Api.Services.Documentos;
using RelatorioIA.Api.Services.IA;

namespace RelatorioIA.Tests;

/// <summary>Modo sem IA: cidades fora do banco e pedidos que devem virar relatório automaticamente.</summary>
public class ModoLocalCidadesTests
{
    static readonly List<CalculadoraRelatorio.Disponibilidade> SoAguas =
        [new("Águas da Prata", 2025, 12, 12), new("Águas da Prata", 2026, 7, 7)];

    static readonly string[] Conhecidos =
        ["aguas da prata", "sao joao da boa vista", "boa vista", "divinolandia", "sao sebastiao da grama", "planalto"];

    sealed class Ambiente(string raiz) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = raiz;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "testes";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = raiz;
        public string EnvironmentName { get; set; } = "Development";
    }

    static AssistenteLocal Local()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "conhecidos-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(raiz, "brasoes"));
        File.WriteAllText(Path.Combine(raiz, "brasoes", "indice-wikidata-35.json"),
            System.Text.Json.JsonSerializer.Serialize(Conhecidos.ToDictionary(n => n, n => "https://x/" + n)));
        var mc = new MunicipiosConhecidos(new Ambiente(raiz), Options.Create(new RelatorioOptions()), new ConfigurationBuilder().Build());
        return new AssistenteLocal(mc);
    }

    [Fact]
    public void Comparativo_com_cidade_fora_do_banco_gera_relatorio_da_disponivel_e_avisa()
    {
        var p = Local().Planejar("preciso de um comparativo entre são joão da boa vista e aguas da prata, para saber os gastos e FPM de cada", SoAguas);
        Assert.Equal("relatorio_gerencial", p.Acao);
        Assert.Equal("Águas da Prata", p.Municipio);
        Assert.Contains("são joão da boa vista não está disponível", p.Mensagem);
        Assert.DoesNotContain("boa vista e", p.Mensagem);   // não lista "boa vista" separado
    }

    [Fact]
    public void So_cidades_fora_do_banco_pede_esclarecimento_com_as_disponiveis()
    {
        var p = Local().Planejar("Relatório de Divinolândia e São Sebastião da Grama de 2025", SoAguas);
        Assert.Equal("esclarecer", p.Acao);
        Assert.Contains("Divinolândia", p.Mensagem);
        Assert.Contains("São Sebastião da Grama", p.Mensagem);
        Assert.Contains("Hoje está disponível: Águas da Prata", p.Mensagem);
    }

    [Theory]
    [InlineData("preciso das despesas de 2025", "relatorio_gerencial")]
    [InlineData("quero ver o FPM de 2025", "relatorio_gerencial")]
    [InlineData("Quanto foi a receita de 2025?", "responder")]
    public void Na_duvida_gera_relatorio_pergunta_direta_responde_no_chat(string msg, string acao) =>
        Assert.Equal(acao, Local().Planejar(msg, SoAguas).Acao);

    [Fact]
    public void Nome_ambiguo_nao_vira_cidade() =>
        Assert.Empty(MunicipiosConhecidos.CitadosForaDoBanco("despesas no planalto de 2025", Conhecidos, ["Águas da Prata"]));
}
