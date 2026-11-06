using RelatorioIA.Api.Services;
using RelatorioIA.Api.Services.IA;

namespace RelatorioIA.Tests;

/// <summary>Modo sem IA (fallback quando o Gemini está fora do ar).</summary>
public class AssistenteLocalTests
{
    static readonly List<CalculadoraRelatorio.Disponibilidade> Catalogo =
    [
        new("Águas da Prata", 2025, 12, 12), new("Águas da Prata", 2026, 9, 9),
        new("São João da Boa Vista", 2025, 12, 12), new("São João da Boa Vista", 2026, 8, 8)
    ];

    static Plano P(string msg) => new AssistenteLocal().Planejar(msg, Catalogo);

    [Fact]
    public void Gerencial_com_periodo_e_comparacao()
    {
        var p = P("Relatório gerencial de janeiro a maio de 2026 comparando com 2025");
        Assert.Equal("relatorio_gerencial", p.Acao);
        Assert.Equal((2026, 1, 5, 2025), (p.Ano, p.MesInicial, p.MesFinal, p.AnoComparacao));
    }

    [Theory]
    [InlineData("relatório do 1º trimestre de 2026", 1, 3)]
    [InlineData("relatório do segundo semestre de 2025", 7, 12)]
    [InlineData("relatório do terceiro quadrimestre de 2025", 9, 12)]
    [InlineData("relatório acumulado até março de 2026", 1, 3)]
    public void Periodos(string msg, int ini, int fim)
    {
        var p = P(msg);
        Assert.Equal((ini, fim), (p.MesInicial, p.MesFinal));
    }

    [Fact]
    public void Municipio_citado_sem_acento()
    {
        Assert.Equal("São João da Boa Vista", P("relatorio de sao joao da boa vista 2025").Municipio);
    }

    [Fact]
    public void Fora_do_escopo_pede_esclarecimento()
    {
        Assert.Equal("esclarecer", P("qual a previsão do tempo amanhã?").Acao);
    }

    [Fact]
    public void Pergunta_financeira_sem_pedir_documento_responde_no_chat()
    {
        Assert.Equal("responder", P("quanto foi a despesa paga em 2025?").Acao);
    }
}
