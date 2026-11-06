using RelatorioIA.Api.Services;
using RelatorioIA.Api.Services.Consultas;
using RelatorioIA.Api.Services.IA;

namespace RelatorioIA.Tests;

/// <summary>Sem IA (Gemini fora do ar), o pedido de projeção ainda precisa funcionar com a consulta padrão.</summary>
[Collection("banco")]
public class ProjecaoLocalTests(BancoFixture f)
{
    [Theory]
    [InlineData("Quero uma projeção dos gastos com saúde até o final de 2027", "funcao = 'SAÚDE'", 2027)]
    [InlineData("Previsão da arrecadação até o fim de 2026", "vw_receitas", 2026)]
    [InlineData("Projeção da despesa total para 2027", "vw_despesas", 2027)]
    public async Task Monta_consulta_valida_com_historico_mensal(string pedido, string trecho, int ano)
    {
        await using var db = f.NovoContexto();
        var cat = await f.Calculadora(db).CatalogoAsync();
        var plano = new Plano { Municipios = [BancoFixture.Municipio] };
        ChatService.ProjecaoLocal(plano, pedido, cat);

        Assert.Equal("relatorio_consulta", plano.Acao);
        Assert.Equal(ano, plano.Projecao!.AteAno);
        var sql = plano.Consultas.Single().Sql;
        Assert.Contains(trecho, sql);
        var exec = new ExecutorSql(f.Config);
        Assert.Null(exec.Validar(sql));
        var r = await exec.ExecutarAsync("historico", "h", sql, default);
        Assert.True(r.Ok, r.Erro);
        Assert.True(r.Linhas.Count >= 36);
        Assert.NotNull(Projetor.LerHistorico(r, "valor"));
    }
}
