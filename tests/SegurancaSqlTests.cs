using Microsoft.Data.SqlClient;
using RelatorioIA.Api.Services.Consultas;

namespace RelatorioIA.Tests;

/// <summary>O SQL gerado pela IA só pode LER as "views" vw_despesas/vw_receitas (CTEs sobre as views do painel).</summary>
[Collection("banco")]
public class SegurancaSqlTests(BancoFixture f)
{
    ExecutorSql Exec => new(f.Config);

    [Theory]
    [InlineData("SELECT municipio, SUM(valor) FROM vw_receitas GROUP BY municipio")]
    [InlineData("WITH t AS (SELECT funcao, SUM(valor_pago) v FROM vw_despesas GROUP BY funcao) SELECT * FROM t ORDER BY v DESC")]
    [InlineData("select TOP 5 credor from vw_despesas where historico like '%drop table%'")]   // palavra proibida dentro de texto é ok
    [InlineData("SELECT d.funcao, r.total FROM vw_despesas d JOIN (SELECT municipio, SUM(valor) total FROM vw_receitas GROUP BY municipio) r ON r.municipio = d.municipio")]
    [InlineData("SELECT ROUND(100.0 * SUM(valor_pago) / NULLIF(SUM(valor_liquidado), 0), 2) AS pct FROM vw_despesas;")]   // ; final é removido
    [InlineData("SELECT credor FROM vw_despesas WHERE elemento_despesa LIKE N'%[0-9]%'")]   // colchetes dentro de texto
    public void Aceita_consultas_validas(string sql) => Assert.Null(Exec.Validar(sql));

    [Theory]
    [InlineData("DELETE FROM vw_despesas")]
    [InlineData("UPDATE vw_despesas SET valor = 0")]
    [InlineData("SELECT 1; DROP TABLE x")]
    [InlineData("SELECT * FROM vw_DespesaCompleta")]                          // view física: só pelas CTEs
    [InlineData("SELECT * FROM dbo.vw_DespesaCompleta")]
    [InlineData("SELECT * FROM Tcudb.dbo.vw_ReceitaCompleta")]
    [InlineData("SELECT * FROM [dbo].[vw_DespesaCompleta]")]
    [InlineData("SELECT * FROM vw_despesas, Usuarios")]                       // join por vírgula
    [InlineData("SELECT * FROM vw_despesas WHERE 1 IN (SELECT 1 FROM sys.objects)")]
    [InlineData("SELECT name FROM sys.tables")]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES")]
    [InlineData("EXEC xp_cmdshell 'dir'")]
    [InlineData("SELECT 1 WAITFOR DELAY '00:00:10'")]
    [InlineData("SELECT * INTO copia FROM vw_despesas")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'y')")]
    [InlineData("SELECT @@VERSION")]
    [InlineData("SELECT * FROM #temp")]
    [InlineData("SELECT * FROM vw_despesas -- comentário")]
    [InlineData("WITH x AS (SELECT 1 a) DELETE FROM vw_despesas")]
    public void Bloqueia_consultas_perigosas(string sql) => Assert.NotNull(Exec.Validar(sql));

    [Fact]
    public void Converte_LIMIT_do_SQLite_em_TOP_e_prefixa_as_CTEs()
    {
        var final = ExecutorSql.MontarSqlFinal("SELECT credor, SUM(valor_pago) AS pago FROM vw_despesas GROUP BY credor ORDER BY pago DESC LIMIT 10");
        Assert.StartsWith("WITH vw_despesas AS (", final);
        Assert.Contains("SELECT TOP 10 credor", final);
        Assert.DoesNotContain("LIMIT", final);

        // Consulta que já começa com WITH: as CTEs do sistema entram antes, separadas por vírgula
        var comWith = ExecutorSql.MontarSqlFinal("WITH t AS (SELECT 1 AS a) SELECT * FROM t");
        Assert.Contains("FROM dbo.vw_ReceitaCompleta),\nt AS (SELECT 1 AS a)", comWith);
    }

    [Fact]
    public async Task Consulta_com_LIMIT_executa_no_SQL_Server()
    {
        var r = await Exec.ExecutarAsync("x", "x",
            "SELECT credor, ROUND(SUM(valor_pago),2) AS pago FROM vw_despesas WHERE municipio='aguas-da-prata' AND ano=2025 GROUP BY credor ORDER BY pago DESC LIMIT 5", default);
        Assert.True(r.Ok, r.Erro);
        Assert.Equal(5, r.Linhas.Count);
    }

    [Fact]
    public async Task Usuario_do_banco_e_somente_leitura()
    {
        // Verificação sem tentar escrever: permissões efetivas do usuário nas views do painel
        await using var con = new SqlConnection(f.ConnectionString);
        await con.OpenAsync();
        await using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT HAS_PERMS_BY_NAME('dbo.vw_DespesaCompleta','OBJECT','SELECT'), " +
                          "HAS_PERMS_BY_NAME('dbo.vw_DespesaCompleta','OBJECT','DELETE'), " +
                          "HAS_PERMS_BY_NAME('dbo.vw_ReceitaCompleta','OBJECT','UPDATE'), " +
                          "HAS_PERMS_BY_NAME(NULL,'DATABASE','CREATE TABLE')";
        await using var rd = await cmd.ExecuteReaderAsync();
        await rd.ReadAsync();
        Assert.Equal((1, 0, 0, 0), (rd.GetInt32(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt32(3)));
    }

    [Fact]
    public async Task Limita_o_numero_de_linhas()
    {
        var r = await Exec.ExecutarAsync("x", "x", "SELECT credor FROM vw_despesas WHERE municipio='aguas-da-prata' AND ano=2025", default);
        Assert.True(r.Ok, r.Erro);
        Assert.Equal(ExecutorSql.LimiteLinhas, r.Linhas.Count);
        Assert.True(r.Truncado);
    }

    [Theory]
    [InlineData("rubrica LIKE '%IMPOSTOS SOBRE O PATRIMONIO%'")]      // banco tem "Patrimônio" (com acento)
    [InlineData("rubrica LIKE '%impostos sobre o patrimônio%'")]
    [InlineData("alinea LIKE '%PARTICIPACAO DOS MUNICIPIOS%'")]
    public async Task Like_ignora_acento_e_maiusculas(string filtro)
    {
        var r = await Exec.ExecutarAsync("x", "x",
            $"SELECT COUNT(*) AS n FROM vw_receitas WHERE municipio='aguas-da-prata' AND ano=2025 AND {filtro}", default);
        Assert.True(r.Ok, r.Erro);
        Assert.True(ExecutorSql.ParaDecimal(r.Linhas[0][0]) > 0, $"nenhuma linha para: {filtro}");
    }
}
