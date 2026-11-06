using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace RelatorioIA.Api.Services.Consultas;

public class ResultadoConsulta
{
    public required string Id { get; init; }
    public required string Objetivo { get; init; }
    public required string Sql { get; init; }
    public List<string> Colunas { get; init; } = [];
    public List<object?[]> Linhas { get; init; } = [];
    public bool Truncado { get; init; }
    public string? Erro { get; init; }
    public bool Ok => Erro == null;
}

/// <summary>
/// Executa no SQL Server (Tcudb) o SQL gerado pela IA, com segurança em camadas:
///  1. validação: só SELECT/WITH, uma instrução, sem comentários, sem comandos/funções perigosas, só as "views"
///     vw_despesas/vw_receitas (que são CTEs sobre as views do painel) — nada de dbo., sys., tabelas físicas;
///  2. usuário do banco somente leitura (dev_report: só SELECT nas views);
///  3. tempo máximo por consulta e limite de linhas lidas.
/// </summary>
public partial class ExecutorSql(IConfiguration cfg)
{
    public const int LimiteLinhas = 500;
    public const int TempoMaximoSegundos = 30;

    public string? Validar(string sql)
    {
        var s = Normalizar(sql);
        if (s.Length == 0) return "SQL vazio.";
        if (!RegexInicio().IsMatch(s)) return "Apenas consultas SELECT (ou WITH ... SELECT) são permitidas.";
        var semTextos = RegexLiteral().Replace(s, "''");
        if (semTextos.Contains(';')) return "Apenas uma instrução SQL por consulta.";
        if (semTextos.Contains("--") || semTextos.Contains("/*")) return "Comentários não são permitidos no SQL.";
        if (Regex.IsMatch(semTextos, @"[""`\[\]@#]")) return "Não use identificadores entre aspas/colchetes, variáveis (@) nem tabelas temporárias (#).";
        var proibida = RegexProibidas().Match(semTextos);
        if (proibida.Success) return $"Palavra não permitida no SQL: {proibida.Value}.";
        if (Regex.IsMatch(semTextos, @"\w\s*\.\s*\w+\s*\.") || Regex.IsMatch(semTextos, @"\b(dbo|sys|information_schema|tcudb|master|msdb|tempdb)\s*\.", RegexOptions.IgnoreCase))
            return $"Use apenas {string.Join(", ", EsquemaConsulta.ViewsPermitidas)} (sem nomes de banco/esquema, como dbo. ou sys.).";

        var ctes = RegexCte().Matches(semTextos).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();
        foreach (Match m in RegexTabela().Matches(semTextos))
        {
            var nome = m.Groups[2].Value.ToLowerInvariant();
            if (!EsquemaConsulta.ViewsPermitidas.Contains(nome) && !ctes.Contains(nome))
                return $"Tabela/view não permitida: {nome}. Use apenas {string.Join(", ", EsquemaConsulta.ViewsPermitidas)}.";
        }
        // Join por vírgula com nome não permitido ("FROM vw_despesas, Tabela")
        foreach (Match m in RegexVirgula().Matches(semTextos))
        {
            var nome = m.Groups[1].Value.ToLowerInvariant();
            if (!EsquemaConsulta.ViewsPermitidas.Contains(nome) && !ctes.Contains(nome))
                return $"Tabela/view não permitida: {nome}. Use apenas {string.Join(", ", EsquemaConsulta.ViewsPermitidas)}.";
        }
        return null;
    }

    /// <summary>Prefixa as CTEs (vw_despesas/vw_receitas) e converte "... LIMIT n" (caso a IA escreva no estilo MySQL/SQLite) em "SELECT TOP n ...".</summary>
    public static string MontarSqlFinal(string sql)
    {
        var s = Normalizar(sql);
        var limite = RegexLimit().Match(s);
        if (limite.Success && RegexInicioSelect().IsMatch(s) && !RegexTop().IsMatch(s))
        {
            s = s[..limite.Index].TrimEnd();
            s = RegexInicioSelect().Replace(s, m => $"SELECT {m.Groups[1].Value}TOP {limite.Groups[1].Value} ", 1);
        }
        var w = RegexWith().Match(s);
        return w.Success
            ? $"WITH {EsquemaConsulta.Ctes},\n{s[w.Length..]}"
            : $"WITH {EsquemaConsulta.Ctes}\n{s}";
    }

    public async Task<ResultadoConsulta> ExecutarAsync(string id, string objetivo, string sql, CancellationToken ct)
    {
        var s = Normalizar(sql);
        var erro = Validar(s);
        if (erro != null) return new ResultadoConsulta { Id = id, Objetivo = objetivo, Sql = s, Erro = erro };

        try
        {
            await using var con = new SqlConnection(cfg.GetConnectionString("Tcudb"));
            await con.OpenAsync(ct);
            await using var cmd = con.CreateCommand();
            cmd.CommandText = MontarSqlFinal(s);
            cmd.CommandTimeout = TempoMaximoSegundos;
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var colunas = Enumerable.Range(0, rd.FieldCount).Select(rd.GetName).ToList();
            var linhas = new List<object?[]>();
            var truncado = false;
            while (await rd.ReadAsync(ct))
            {
                if (linhas.Count >= LimiteLinhas) { truncado = true; cmd.Cancel(); break; }
                var vals = new object?[rd.FieldCount];
                for (int i = 0; i < rd.FieldCount; i++)
                    vals[i] = rd.IsDBNull(i) ? null : rd.GetValue(i) switch
                    {
                        decimal dm => (double)Math.Round(dm, 2),
                        double dv => Math.Round(dv, 2),
                        float f => Math.Round((double)f, 2),
                        DateTime dt => dt.ToString("yyyy-MM-dd"),
                        DateOnly d => d.ToString("yyyy-MM-dd"),
                        short sh => (long)sh,
                        int it => (long)it,
                        byte by => (long)by,
                        var o => o
                    };
                linhas.Add(vals);
            }
            return new ResultadoConsulta { Id = id, Objetivo = objetivo, Sql = s, Colunas = colunas, Linhas = linhas, Truncado = truncado };
        }
        catch (SqlException ex) when (ex.Number == -2)   // timeout do cliente
        {
            return new ResultadoConsulta { Id = id, Objetivo = objetivo, Sql = s,
                Erro = $"A consulta passou de {TempoMaximoSegundos}s e foi interrompida. Simplifique (evite JOIN entre as views; agregue com GROUP BY e filtre município/ano)." };
        }
        catch (SqlException ex)
        {
            return new ResultadoConsulta { Id = id, Objetivo = objetivo, Sql = s, Erro = $"Erro do SQL Server: {ex.Message}" };
        }
    }

    static string Normalizar(string sql) => (sql ?? "").Trim().TrimEnd(';').Trim();

    public static bool Numerico(object? v) => v is long or int or double or decimal or float or short;
    public static decimal ParaDecimal(object? v) => v switch
    {
        long l => l, int i => i, short sh => sh, double d => (decimal)d, decimal m => m, float f => (decimal)f,
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var x) => x,
        _ => 0m
    };

    [GeneratedRegex(@"^\s*(select|with)\b", RegexOptions.IgnoreCase)] private static partial Regex RegexInicio();
    [GeneratedRegex(@"^\s*select\s+(distinct\s+)?", RegexOptions.IgnoreCase)] private static partial Regex RegexInicioSelect();
    [GeneratedRegex(@"^\s*with\s+", RegexOptions.IgnoreCase)] private static partial Regex RegexWith();
    [GeneratedRegex(@"\btop\s*\(?\s*\d+", RegexOptions.IgnoreCase)] private static partial Regex RegexTop();
    [GeneratedRegex(@"\s+limit\s+(\d+)\s*$", RegexOptions.IgnoreCase)] private static partial Regex RegexLimit();
    [GeneratedRegex(@"N?'(?:[^']|'')*'")] private static partial Regex RegexLiteral();
    [GeneratedRegex(@"\b(insert|update|delete|drop|alter|create|truncate|merge|into|exec|execute|grant|revoke|deny|backup|restore|dbcc|kill|shutdown|waitfor|declare|use|openrowset|opendatasource|openquery|openxml|bulk|xp_\w+|sp_\w+|fn_\w+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RegexProibidas();
    [GeneratedRegex(@"\b(\w+)\s+as\s*\(", RegexOptions.IgnoreCase)] private static partial Regex RegexCte();
    [GeneratedRegex(@"\b(from|join)\s+([A-Za-z_][\w]*)", RegexOptions.IgnoreCase)] private static partial Regex RegexTabela();
    [GeneratedRegex(@"\bfrom\s+[A-Za-z_]\w*(?:\s+(?:as\s+)?[A-Za-z_]\w*)?\s*,\s*([A-Za-z_]\w*)", RegexOptions.IgnoreCase)] private static partial Regex RegexVirgula();
}
