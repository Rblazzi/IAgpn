using RelatorioIA.Api.Services.Consultas;

namespace RelatorioIA.Tests;

/// <summary>Projeção determinística (a IA nunca calcula): conferida com números feitos à mão.</summary>
public class ProjetorTests
{
    // 2025: 100/mês (completo). 2026: 110/mês de jan a set → crescimento de 10%.
    static List<PontoMensal> Historico() =>
        Enumerable.Range(1, 12).Select(m => new PontoMensal(2025, m, 100m))
            .Concat(Enumerable.Range(1, 9).Select(m => new PontoMensal(2026, m, 110m))).ToList();

    // "Hoje" = outubro/2026: o histórico vai até setembro, então outubro em diante pode ser projetado
    static readonly DateOnly Out2026 = new(2026, 10, 15);

    [Fact]
    public void Mes_passado_nao_publicado_nunca_recebe_valor_estimado()
    {
        // Hoje = 04/11/2026; dados até setembro → outubro/2026 JÁ PASSOU e não foi publicado
        var p = Projetor.Projetar(Historico(), 2027, 12, new DateOnly(2026, 11, 4))!;
        var out26 = p.Meses.Single(m => m is { Ano: 2026, Mes: 10 });
        Assert.True(out26.SemDados);
        Assert.Null(out26.Realizado); Assert.Null(out26.Tendencia); Assert.Null(out26.Estavel);
        Assert.Equal(110m, p.Meses.Single(m => m is { Ano: 2026, Mes: 11 }).Tendencia);   // novembro: projetado

        var a26 = p.Anos.Single(a => a.Ano == 2026);
        Assert.Equal(1, a26.MesesSemDados);
        Assert.Equal(990m + 110m + 110m, a26.TotalTendencia);    // outubro fora do total
        Assert.Contains("out/2026", p.Metodo);

        // Os meses realizados nunca mudam
        Assert.All(p.Meses.Where(m => m.Realizado != null), m => Assert.True(m.Realizado is 100m or 110m));

        // Na tabela/gráfico: "Sem dados publicados", sem nenhum valor
        var (mensal, _, _) = Projetor.ComoResultados(p, "Saúde");
        var linhaOut = mensal.Linhas.Single(l => (string)l[0]! == "OUT/2026");
        Assert.Equal("Sem dados publicados", linhaOut[1]);
        Assert.All(linhaOut.Skip(2), Assert.Null);
    }

    [Fact]
    public void Pico_isolado_nao_distorce_a_taxa()
    {
        // Empenho global em março (500 vs ~105): pela soma daria +65%; pela mediana mês a mês, +5%
        var h = Enumerable.Range(1, 12).Select(m => new PontoMensal(2025, m, 100m))
            .Concat(Enumerable.Range(1, 7).Select(m => new PontoMensal(2026, m, m == 3 ? 500m : 105m))).ToList();
        var p = Projetor.Projetar(h, 2027, 12, new DateOnly(2026, 8, 1))!;
        Assert.Equal(0.05m, p.TaxaCrescimento);
        Assert.Contains("mediana", p.Metodo);
    }

    [Fact]
    public void Estudo_estatistico_media_desvio_padrao_e_margem_de_erro()
    {
        // Variações: +10%, −10%, +5%, −5% → média 0; desvio padrão amostral √(250/3) = 9,13 p.p.
        // Erro padrão = 9,1287 / √4 = 4,5644; t(3 g.l., 95%) = 3,182 → margem ± 14,52 p.p.
        var h = Enumerable.Range(1, 12).Select(m => new PontoMensal(2025, m, 100m))
            .Concat(new[] { 110m, 90m, 105m, 95m }.Select((v, i) => new PontoMensal(2026, i + 1, v))).ToList();
        var p = Projetor.Projetar(h, 2026, 12, new DateOnly(2026, 5, 1))!;
        var e = p.Estatistica!;
        Assert.Equal(4, e.Meses);
        Assert.Equal(0m, e.Media);
        Assert.Equal(9.13m, e.DesvioPadrao);
        Assert.Equal(0m, e.Mediana);             // mediana de 0,90/0,95/1,05/1,10 = 1,00 → taxa 0%
        Assert.Equal(3.182m, e.T);
        Assert.Equal(14.52m, e.Margem);

        // Margem em R$ do ano: 8 meses projetados (mai–dez) com base de 100 cada → 800 × 14,52% = 116,16
        var a26 = p.Anos.Single(a => a.Ano == 2026);
        Assert.Equal(116.16m, a26.MargemErro);
        Assert.Equal(0m, p.Anos.Single(a => a.Ano == 2025).MargemErro);   // ano só realizado: sem margem
        Assert.Contains("margem de erro", p.Metodo);
        Assert.DoesNotContain("Nota metodológica", p.Metodo);
    }

    [Fact]
    public void Sem_dispersao_a_margem_e_zero_e_com_um_mes_nao_ha_estatistica()
    {
        var p = Projetor.Projetar(Historico(), 2027, 12, Out2026)!;   // todas as variações = +10%
        Assert.Equal(0m, p.Estatistica!.DesvioPadrao);
        Assert.Equal(0m, p.Estatistica.Margem);

        var umMes = Enumerable.Range(1, 12).Select(m => new PontoMensal(2025, m, 100m)).Append(new PontoMensal(2026, 1, 110m)).ToList();
        Assert.Null(Projetor.Projetar(umMes, 2026, 12, new DateOnly(2026, 2, 1))!.Estatistica);
    }

    [Fact]
    public void Nao_projeta_horizonte_que_ja_passou()
    {
        // Pedir "projeção até out/2026" em 04/11/2026: é passado → recusa
        Assert.Null(Projetor.Projetar(Historico(), 2026, 10, new DateOnly(2026, 11, 4)));
    }

    [Fact]
    public void Calcula_taxa_pelos_mesmos_meses_e_projeta_os_dois_cenarios()
    {
        var p = Projetor.Projetar(Historico(), 2027, 12, Out2026)!;
        Assert.Equal(0.10m, p.TaxaCrescimento);

        var out26 = p.Meses.Single(m => m is { Ano: 2026, Mes: 10 });
        Assert.Null(out26.Realizado);
        Assert.Equal(110m, out26.Tendencia);        // 100 (out/2025) × 1,10
        Assert.Equal(100m, out26.Estavel);          // repete out/2025

        var jan27 = p.Meses.Single(m => m is { Ano: 2027, Mes: 1 });
        Assert.Equal(121m, jan27.Tendencia);        // 110 (jan/2026 realizado) × 1,10
        Assert.Equal(110m, jan27.Estavel);
    }

    [Fact]
    public void Calcula_totais_anuais_e_percentual_realizado()
    {
        var p = Projetor.Projetar(Historico(), 2027, 12, Out2026)!;
        var a26 = p.Anos.Single(a => a.Ano == 2026);
        Assert.Equal(990m, a26.Realizado);           // 9 × 110
        Assert.Equal(1320m, a26.TotalTendencia);     // 990 + 3 × 110
        Assert.Equal(1290m, a26.TotalEstavel);       // 990 + 3 × 100
        Assert.Equal(75m, a26.PercentualRealizado);  // 990 / 1320
        Assert.Equal(9, a26.MesesRealizados);

        var a27 = p.Anos.Single(a => a.Ano == 2027);
        Assert.Equal(0m, a27.Realizado);
        Assert.Equal(0m, a27.PercentualRealizado);
        Assert.Equal(1452m, a27.TotalTendencia);     // 1320 × 1,10
        Assert.Equal(12, p.Meses.Count(m => m.Ano == 2027));
    }

    [Fact]
    public void Recusa_sem_ano_completo_ou_horizonte_no_passado()
    {
        var semAnoCompleto = Enumerable.Range(1, 9).Select(m => new PontoMensal(2026, m, 110m)).ToList();
        Assert.Null(Projetor.Projetar(semAnoCompleto, 2027, 12, Out2026));
        Assert.Null(Projetor.Projetar(Historico(), 2026, 9, Out2026));   // já realizado
        Assert.Null(Projetor.Projetar([], 2027, 12, Out2026));
    }

    [Fact]
    public void Limita_taxa_e_horizonte()
    {
        var explosivo = Enumerable.Range(1, 12).Select(m => new PontoMensal(2025, m, 1m))
            .Concat(Enumerable.Range(1, 3).Select(m => new PontoMensal(2026, m, 1000m))).ToList();
        var p = Projetor.Projetar(explosivo, 2040, 12, new DateOnly(2026, 4, 1))!;
        Assert.Equal(Projetor.TaxaMaxima, p.TaxaCrescimento);   // limitada a +100% a.a.
        Assert.Equal(2029, p.Meses.Max(m => m.Ano));            // máximo 3 anos à frente
    }

    [Fact]
    public void Gera_resultados_para_tabela_e_grafico_continuos()
    {
        var p = Projetor.Projetar(Historico(), 2027, 12, Out2026)!;
        var (mensal, anual, _) = Projetor.ComoResultados(p, "Saúde");
        var set26 = mensal.Linhas.Single(l => (string)l[0]! == "SET/2026");
        Assert.Equal(110.0, set26[2]);    // realizado
        Assert.Equal(110.0, set26[3]);    // a projeção parte do último realizado (linha contínua)
        Assert.Null(mensal.Linhas.Single(l => (string)l[0]! == "OUT/2026")[2]);   // sem realizado no futuro
        Assert.Equal("2026", anual.Linhas.Single(l => (string)l[0]! == "2026")[0]);
        Assert.Equal(75.0, anual.Linhas.Single(l => (string)l[0]! == "2026")[6]);
    }

    [Fact]
    public void Le_historico_de_consulta_com_ano_mes_valor()
    {
        var r = new ResultadoConsulta
        {
            Id = "c1", Objetivo = "h", Sql = "s", Colunas = ["ano", "mes", "valor"],
            Linhas = [[2025L, 1L, 10.5], [2025L, 2L, 20.0]]
        };
        var h = Projetor.LerHistorico(r, "valor")!;
        Assert.Equal(2, h.Count);
        Assert.Equal(new PontoMensal(2025, 1, 10.5m), h[0]);
    }
}
