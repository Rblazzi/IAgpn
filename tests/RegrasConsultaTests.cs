using RelatorioIA.Api.Services;
using RelatorioIA.Api.Services.Consultas;

namespace RelatorioIA.Tests;

/// <summary>Regras conferidas pelo sistema no SQL da IA antes de executar.</summary>
public class RegrasConsultaTests
{
    static readonly List<CalculadoraRelatorio.Disponibilidade> Cat =
    [
        new("Águas da Prata", 2025, 12, 12, Codigo: "aguas-da-prata"), new("Águas da Prata", 2026, 7, 7, Codigo: "aguas-da-prata"),
        new("São João da Boa Vista", 2025, 12, 12, Codigo: "sao-joao-da-boa-vista"), new("São João da Boa Vista", 2026, 9, 9, Codigo: "sao-joao-da-boa-vista"),
    ];

    [Theory]
    // o caso real: comparativo jan–set/2026, mas Águas da Prata só tem até julho
    [InlineData("SELECT mes, SUM(valor_pago) FROM vw_despesas WHERE municipio IN ('aguas-da-prata','sao-joao-da-boa-vista') AND ano = 2026 AND mes BETWEEN 1 AND 9 GROUP BY mes", "julho")]
    [InlineData("SELECT SUM(valor) FROM vw_receitas WHERE municipio='aguas-da-prata' AND ano=2026 AND mes <= 8", "julho")]
    // o caso real: JOIN direto entre despesas e receitas multiplica os totais
    [InlineData("SELECT d.municipio, SUM(d.valor_pago), SUM(r.valor) FROM vw_despesas d LEFT JOIN vw_receitas r ON d.municipio = r.municipio AND d.mes = r.mes WHERE d.municipio IN ('aguas-da-prata') AND d.ano = 2025 GROUP BY d.municipio", "JOIN")]
    [InlineData("SELECT SUM(valor) FROM vw_receitas WHERE municipio IN ('aguas-da-prata','divinolandia') AND ano=2025", "divinolandia")]
    public void Recusa(string sql, string trecho) => Assert.Contains(trecho, RegrasConsulta.Problema(sql, Cat));

    [Theory]
    [InlineData("SELECT mes, SUM(valor_pago) FROM vw_despesas WHERE municipio IN ('aguas-da-prata','sao-joao-da-boa-vista') AND ano = 2026 AND mes BETWEEN 1 AND 7 GROUP BY mes")]
    [InlineData("SELECT SUM(valor) FROM vw_receitas WHERE municipio='sao-joao-da-boa-vista' AND ano=2026 AND mes <= 9")]
    [InlineData("WITH d AS (SELECT municipio, SUM(valor_pago) p FROM vw_despesas WHERE ano=2025 GROUP BY municipio), r AS (SELECT municipio, SUM(valor) v FROM vw_receitas WHERE ano=2025 GROUP BY municipio) SELECT d.municipio, d.p, r.v FROM d JOIN r ON r.municipio = d.municipio")]
    [InlineData("SELECT ano, mes, SUM(valor_pago) AS valor FROM vw_despesas WHERE municipio='aguas-da-prata' AND ano >= 2022 GROUP BY ano, mes")]
    public void Aceita(string sql) => Assert.Null(RegrasConsulta.Problema(sql, Cat));
}
