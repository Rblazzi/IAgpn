using RelatorioIA.Api.Services.Consultas;
using RelatorioIA.Api.Services.Documentos;
using RelatorioIA.Api.Services.IA;

namespace RelatorioIA.Tests;

/// <summary>Gráfico escolhido pelo sistema quando a IA não pede um (toda seção com dados termina com gráfico).</summary>
public class GraficosAutomaticosTests
{
    static ResultadoConsulta R(string[] colunas, params object?[][] linhas) =>
        new() { Id = "q", Objetivo = "teste", Sql = "", Colunas = [.. colunas], Linhas = [.. linhas] };

    [Fact]
    public void ParteDoTodo_AteCincoItens_ViraPizza()
    {
        var g = MontadorConsulta.GraficoAutomatico(R(["categoria", "valor"],
            ["Correntes", 900m], ["Capital", 100m], ["Intra", 50m]));
        Assert.Equal(TipoGrafico.Rosca, g!.Tipo);
    }

    [Fact]
    public void Ranking_MaisDeCincoItens_ViraBarras()
    {
        var linhas = Enumerable.Range(1, 12).Select(i => new object?[] { $"Credor {i}", (decimal)(1000 - i) }).ToArray();
        var g = MontadorConsulta.GraficoAutomatico(R(["credor", "valor_pago"], linhas));
        Assert.Equal(TipoGrafico.Barras, g!.Tipo);
    }

    [Fact]
    public void Mensal_ViraColunasPorMes()
    {
        var g = MontadorConsulta.GraficoAutomatico(R(["mes", "mes_nome", "valor"],
            [1, "Janeiro", 10m], [2, "Fevereiro", 20m], [3, "Março", 30m]));
        Assert.Equal(TipoGrafico.Colunas, g!.Tipo);
        Assert.Equal(["Janeiro", "Fevereiro", "Março"], g.Categorias);
    }

    [Fact]
    public void Comparacao_AnoPorMes_UmaSeriePorAno()
    {
        var g = MontadorConsulta.GraficoAutomatico(R(["municipio", "ano", "mes_nome", "valor"],
            ["aguas-da-prata", 2025, "Janeiro", 10m], ["aguas-da-prata", 2025, "Fevereiro", 12m],
            ["aguas-da-prata", 2026, "Janeiro", 11m], ["aguas-da-prata", 2026, "Fevereiro", 15m]));
        Assert.Equal(["2025", "2026"], g!.Series.Select(s => s.Nome));
        Assert.Equal(["Janeiro", "Fevereiro"], g.Categorias);
    }

    [Fact]
    public void Comparacao_MunicipiosLadoALado()
    {
        var g = MontadorConsulta.GraficoAutomatico(R(["funcao", "aguas_da_prata", "sao_joao", "pct_aguas"],
            ["Saúde", 100m, 900m, 30m], ["Educação", 80m, 700m, 25m], ["Urbanismo", 50m, 300m, 15m]));
        Assert.Equal(TipoGrafico.Barras, g!.Tipo);
        Assert.Equal(2, g.Series.Count);   // a coluna de % não entra junto com R$
    }

    [Fact]
    public void UmaLinhaComVariosValores_UmaColunaPorValor()
    {
        var g = MontadorConsulta.GraficoAutomatico(R(["empenhado", "liquidado", "pago"], [300m, 200m, 150m]));
        Assert.Equal(3, g!.Categorias.Length);
    }

    [Fact]
    public void ValorUnico_SemGrafico() =>
        Assert.Null(MontadorConsulta.GraficoAutomatico(R(["total"], [123m])));

    [Fact]
    public void GraficoDaIA_ComColunasDePercentual_NaoViraReais()
    {
        // o caso real: comparativo de participação por função com formato "moeda" → aparecia "R$ 30"
        var gp = new GraficoPlanejado { ConsultaId = "q", Tipo = "colunas", CampoCategoria = "funcao",
            CamposValor = ["aguas_da_prata_pct", "sao_joao_da_boa_vista_pct"] };
        var linhas = Enumerable.Range(1, 10).Select(i => new object?[] { $"Função {i}", 30m - i, 25m - i }).ToArray();
        var g = MontadorConsulta.MontarGrafico(gp, R(["funcao", "aguas_da_prata_pct", "sao_joao_da_boa_vista_pct"], linhas));
        Assert.Equal("percentual", g!.Formato);
        Assert.Equal(TipoGrafico.Barras, g.Tipo);   // nomes longos: barras horizontais, sem cortar o rótulo
    }

    [Fact]
    public void PizzaPedidaPelaIA()
    {
        var gp = new GraficoPlanejado { ConsultaId = "q", Tipo = "pizza", CampoCategoria = "orgao", CamposValor = ["valor"] };
        var g = MontadorConsulta.MontarGrafico(gp, R(["orgao", "valor"], ["Prefeitura", 900m], ["Câmara", 100m]));
        Assert.Equal(TipoGrafico.Rosca, g!.Tipo);
        Assert.NotNull(GeradorGraficos.Desenhar(g).png);
    }
}
