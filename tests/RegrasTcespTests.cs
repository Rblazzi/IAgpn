using RelatorioIA.Api.Models;
using RelatorioIA.Api.Services.Consultas;

namespace RelatorioIA.Tests;

/// <summary>
/// Gabarito: "Águas da Prata Relatório Gerencial - 26-08-2026.pdf" (jan–mai/2026 × jan–mai/2025), conferido
/// contra o banco REAL do painel (Tcudb). Se algum destes testes quebrar, a regra de cálculo mudou ou o banco
/// foi alterado, e os relatórios deixaram de bater com o oficial.
/// </summary>
[Collection("banco")]
public class RegrasTcespTests(BancoFixture f)
{
    const string Municipio = BancoFixture.Municipio;

    async Task<DadosRelatorio> Exemplo()
    {
        await using var db = f.NovoContexto();
        return await f.Calculadora(db).CalcularAsync(new ParametrosRelatorio(Municipio, 2026, 1, 5, 2025));
    }

    [Fact]
    public async Task Receita_consolidada_bate_com_o_relatorio_oficial()
    {
        var r = (await Exemplo()).Receita!;
        Assert.Equal(27_196_080.13m, r.Total);
        Assert.Equal(5_486_451.04m, r.PorMes[1]);
        Assert.Equal(5_816_709.02m, r.PorMes[4]);
        Assert.Equal(19_254_904.71m, r.PorFonte.Linhas.Single(l => l.Rotulo == "TESOURO").Total);
        Assert.Equal(3_900_464.96m, r.PorFonte.Linhas.Single(l => l.Rotulo == "RECURSOS PRÓPRIOS DA ADMINISTRAÇÃO INDIRETA").Total);
        Assert.Equal(2_147_489.83m, r.PorCategoria.Linhas.Single(l => l.Rotulo == "Receitas Correntes - Intra OFSS").Total);
        var jan = r.RubricasPorMes.Single(x => x.Mes == 1).Top[0];
        Assert.StartsWith("17210000", jan.Rotulo);
        Assert.Equal(1_446_617.45m, jan.Valor);
    }

    [Fact]
    public async Task Despesa_consolidada_bate_com_o_relatorio_oficial()
    {
        var s = (await Exemplo()).Despesa!;
        Assert.Equal(31_388_508.42m, s.Empenhado);
        Assert.Equal(80_000.86m, s.Reforco);
        Assert.Equal(235_643.77m, s.Anulacao);
        Assert.Equal(31_232_865.51m, s.Consolidada);           // Empenhado + Reforço − Anulação
        Assert.Equal(25_397_770.84m, s.Liquidado);
        Assert.Equal(22_960_898.02m, s.Pago);
        Assert.Equal(7_617_243.53m, s.ConsolidadaPorMes[1]);
        Assert.Equal(14_082_768.65m, s.PorModalidade.Linhas.Single(l => l.Rotulo == "OUTROS/NÃO APLICÁVEL").Total);
        Assert.Equal(8_738_951.69m, s.PorFuncao.Single(x => x.Rotulo == "SAÚDE").Valor);
        Assert.Equal(8_018_312.69m, s.PorSubfuncao.Single(x => x.Rotulo == "ATENÇÃO BÁSICA").Valor);
        Assert.Equal(27_558_788.90m, s.PorOrgao.Linhas.Single(l => l.Rotulo.StartsWith("PREFEITURA")).Total);
    }

    [Fact]
    public async Task Comparativo_2025_bate_com_o_relatorio_oficial()
    {
        var c = (await Exemplo()).Comparativo!;
        Assert.Equal(23_694_142.93m, c.ReceitaBase);
        Assert.Equal(23_986_333.94m, c.DespesaBase);
        Assert.Equal(20_072_412.56m, c.PagoBase);
        Assert.Equal(6_010_666.67m, c.DespesaPorFuncao.Single(x => x.Rotulo == "SAÚDE").Base);
        Assert.Equal(5_061_310.26m, c.PagoPorFuncao.Single(x => x.Rotulo == "SAÚDE").Base);
        Assert.Equal(15_495_381.44m, c.ReceitaPorFonte.Single(x => x.Rotulo == "TESOURO").Base);
    }

    [Fact]
    public async Task Views_da_IA_dao_os_mesmos_totais_da_calculadora()
    {
        var exec = new ExecutorSql(f.Config);
        var rec = await exec.ExecutarAsync("r", "receita", "SELECT ROUND(SUM(valor),2) AS t FROM vw_receitas WHERE municipio='aguas-da-prata' AND ano=2026 AND mes BETWEEN 1 AND 5", default);
        var des = await exec.ExecutarAsync("d", "despesa", "SELECT ROUND(SUM(valor_empenhado_consolidado),2) AS c, ROUND(SUM(valor_liquidado),2) AS l, ROUND(SUM(valor_pago),2) AS p FROM vw_despesas WHERE municipio='aguas-da-prata' AND ano=2026 AND mes<=5", default);
        Assert.True(rec.Ok, rec.Erro);
        Assert.True(des.Ok, des.Erro);
        Assert.Equal(27_196_080.13m, ExecutorSql.ParaDecimal(rec.Linhas[0][0]));
        Assert.Equal(31_232_865.51m, ExecutorSql.ParaDecimal(des.Linhas[0][0]));
        Assert.Equal(25_397_770.84m, ExecutorSql.ParaDecimal(des.Linhas[0][1]));
        Assert.Equal(22_960_898.02m, ExecutorSql.ParaDecimal(des.Linhas[0][2]));
    }

    [Fact]
    public async Task Fonte_de_recurso_e_categoria_sem_codigo_nas_views()
    {
        var exec = new ExecutorSql(f.Config);
        var r = await exec.ExecutarAsync("x", "x", "SELECT DISTINCT fonte_recurso, categoria FROM vw_receitas WHERE municipio='aguas-da-prata' AND ano=2026", default);
        Assert.True(r.Ok, r.Erro);
        Assert.Contains(r.Linhas, l => (string)l[0]! == "TESOURO");
        Assert.DoesNotContain(r.Linhas, l => ((string)l[0]!).StartsWith("01 -") || ((string)l[1]!).StartsWith("10000000"));
    }

    [Fact]
    public async Task Catalogo_traz_nome_codigo_historico_e_meses_completos()
    {
        await using var db = f.NovoContexto();
        var cat = await f.Calculadora(db).CatalogoAsync();
        Assert.Contains(cat, c => c is { Municipio: Municipio, Codigo: BancoFixture.Codigo, Ano: 2014 });   // histórico desde 2014
        Assert.Contains(cat, c => c is { Municipio: Municipio, Ano: 2025, MesMaxReceita: 12, MesMaxDespesa: 12 });
        // 2026: agosto só tem Câmara e RPPS (a Prefeitura ainda não enviou) → completo até julho
        var d26 = cat.Single(c => c is { Municipio: Municipio, Ano: 2026 });
        Assert.Equal(7, d26.MesMaxDespesa);
        Assert.Equal(7, d26.MesMaxReceita);
        Assert.True(d26.MesParcialDespesa >= 8);
        Assert.Contains("PREFEITURA", d26.Pendencias);
    }

    [Fact]
    public async Task Aceita_nome_ou_codigo_do_municipio()
    {
        await using var db = f.NovoContexto();
        var calc = f.Calculadora(db);
        Assert.Equal(BancoFixture.Codigo, await calc.CodigoAsync("Águas da Prata"));
        Assert.Equal(BancoFixture.Codigo, await calc.CodigoAsync("aguas-da-prata"));
    }
}
