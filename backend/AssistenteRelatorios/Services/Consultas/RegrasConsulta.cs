using System.Text.RegularExpressions;
using RelatorioIA.Api.Models;

namespace RelatorioIA.Api.Services.Consultas;

/// <summary>
/// Regras de negócio conferidas pelo SISTEMA no SQL da IA, antes de executar (além da segurança do ExecutorSql).
/// Quando uma regra é violada, a mensagem volta para a IA corrigir a consulta.
/// </summary>
public static partial class RegrasConsulta
{
    public static string? Problema(string sql, IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogo)
    {
        // 1. Só municípios que existem no banco
        var codigos = catalogo.Select(c => c.Codigo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // códigos usados nos filtros de município: municipio = 'x' / municipio IN ('x','y') / CASE WHEN municipio = 'x'
        var citados = RegexMunicipioIgual().Matches(sql).Select(m => m.Groups[1].Value)
            .Concat(RegexMunicipioIn().Matches(sql).SelectMany(m => RegexLiteral().Matches(m.Groups[1].Value).Select(l => l.Groups[1].Value)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inexistentes = citados.Where(c => !codigos.Contains(c)).ToList();
        if (inexistentes.Count > 0)
            return $"Município(s) {string.Join(", ", inexistentes.Select(c => $"'{c}'"))} não existe(m) no banco. " +
                   $"Use somente: {string.Join(", ", codigos.Select(c => $"'{c}'"))}.";

        // 2. Não juntar despesas e receitas linha a linha (multiplica os valores)
        if (RegexJoinViews().IsMatch(sql))
            return "Não faça JOIN direto entre vw_despesas e vw_receitas: cada linha de uma se repete para cada linha da outra e " +
                   "os totais ficam multiplicados. Agregue cada view numa CTE (ex.: por municipio ou por mes) e junte as CTEs, " +
                   "ou faça consultas separadas.";

        // 3. Período: não passar do último mês COMPLETO de cada município citado (comparação usa o período em comum)
        var municipios = citados.Count > 0
            ? catalogo.Where(c => citados.Contains(c.Codigo, StringComparer.OrdinalIgnoreCase)).ToList()
            : catalogo.ToList();
        foreach (Match m in RegexAno().Matches(sql))
        {
            var ano = int.Parse(m.Groups[1].Value);
            var mesPedido = MesFinal(sql);
            if (mesPedido is null) continue;
            var doAno = municipios.Where(c => c.Ano == ano).ToList();
            if (doAno.Count == 0) continue;
            var limite = doAno.Min(c => Math.Min(c.MesMaxReceita ?? 12, c.MesMaxDespesa ?? 12));
            if (mesPedido > limite)
            {
                var quem = doAno.Where(c => Math.Min(c.MesMaxReceita ?? 12, c.MesMaxDespesa ?? 12) == limite).Select(c => c.Municipio);
                return $"Os dados completos de {ano} vão só até {Fmt.MesTitulo(limite).ToLower(Fmt.PtBr)} (mês {limite}) para " +
                       $"{string.Join(", ", quem)}. Use mes <= {limite} para TODOS os municípios da consulta (período em comum) " +
                       "e informe isso em \"mensagem\".";
            }
        }
        return null;
    }

    /// <summary>Último mês filtrado no SQL (BETWEEN x AND y, &lt;= y, &lt; y, IN (...), = y), se houver.</summary>
    static int? MesFinal(string sql)
    {
        var valores = new List<int>();
        foreach (Match m in RegexMesBetween().Matches(sql)) valores.Add(int.Parse(m.Groups[1].Value));
        foreach (Match m in RegexMesMenorIgual().Matches(sql)) valores.Add(int.Parse(m.Groups[1].Value));
        foreach (Match m in RegexMesMenor().Matches(sql)) valores.Add(int.Parse(m.Groups[1].Value) - 1);
        foreach (Match m in RegexMesIgual().Matches(sql)) valores.Add(int.Parse(m.Groups[1].Value));
        foreach (Match m in RegexMesIn().Matches(sql))
            valores.AddRange(m.Groups[1].Value.Split(',').Select(v => int.TryParse(v.Trim(), out var n) ? n : 0));
        return valores.Count > 0 ? valores.Max() : null;
    }

    [GeneratedRegex(@"\bmunicipio\s*=\s*N?'([^']*)'", RegexOptions.IgnoreCase)] private static partial Regex RegexMunicipioIgual();
    [GeneratedRegex(@"\bmunicipio\s+(?:NOT\s+)?IN\s*\(([^)]*)\)", RegexOptions.IgnoreCase)] private static partial Regex RegexMunicipioIn();
    [GeneratedRegex(@"'([^']*)'")] private static partial Regex RegexLiteral();
    [GeneratedRegex(@"vw_(?:despesas|receitas)\b[^()]*?\bJOIN\s+vw_(?:despesas|receitas)\b", RegexOptions.IgnoreCase)] private static partial Regex RegexJoinViews();
    [GeneratedRegex(@"\bano\s*=\s*(\d{4})\b", RegexOptions.IgnoreCase)] private static partial Regex RegexAno();
    [GeneratedRegex(@"\bmes\s+BETWEEN\s+\d{1,2}\s+AND\s+(\d{1,2})\b", RegexOptions.IgnoreCase)] private static partial Regex RegexMesBetween();
    [GeneratedRegex(@"\bmes\s*<=\s*(\d{1,2})\b", RegexOptions.IgnoreCase)] private static partial Regex RegexMesMenorIgual();
    [GeneratedRegex(@"\bmes\s*<\s*(\d{1,2})\b", RegexOptions.IgnoreCase)] private static partial Regex RegexMesMenor();
    [GeneratedRegex(@"\bmes\s*=\s*(\d{1,2})\b", RegexOptions.IgnoreCase)] private static partial Regex RegexMesIgual();
    [GeneratedRegex(@"\bmes\s+IN\s*\(([\d,\s]+)\)", RegexOptions.IgnoreCase)] private static partial Regex RegexMesIn();
}
