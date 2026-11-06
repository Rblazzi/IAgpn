using RelatorioIA.Api.Models;

namespace RelatorioIA.Api.Services.IA;

/// <summary>
/// Monta um resumo compacto (já formatado em pt-BR) dos números calculados, para enviar à IA.
/// Os valores vão prontos como texto para a IA apenas citá-los — nunca recalcular.
/// </summary>
public static class ResumoParaIA
{
    public static object Montar(DadosRelatorio d)
    {
        var p = d.Parametros;
        var meses = d.Meses;
        object? receita = null, despesa = null, comparativo = null;

        if (d.Receita is { } r)
        {
            var porMes = meses.Where(r.PorMes.ContainsKey).ToDictionary(Fmt.MesTitulo, m => Fmt.Moeda(r.PorMes[m]));
            var maior = r.PorMes.OrderByDescending(x => x.Value).First();
            var menor = r.PorMes.OrderBy(x => x.Value).First();
            receita = new
            {
                total = Fmt.Moeda(r.Total),
                media_mensal = Fmt.Moeda(r.Total / Math.Max(1, r.PorMes.Count)),
                maior_mes = $"{Fmt.MesTitulo(maior.Key)} ({Fmt.Moeda(maior.Value)})",
                menor_mes = $"{Fmt.MesTitulo(menor.Key)} ({Fmt.Moeda(menor.Value)})",
                por_mes = porMes,
                por_fonte = Linhas(r.PorFonte, r.Total),
                por_orgao = Linhas(r.PorOrgao, r.Total),
                por_categoria = Linhas(r.PorCategoria, r.Total),
                principais_rubricas_periodo = r.PrincipaisRubricasPeriodo.Select(x => $"{x.Rotulo}: {Fmt.Moeda(x.Valor)} ({Fmt.Pct(Pct(x.Valor, r.Total))})"),
                rubricas_por_mes = r.RubricasPorMes.ToDictionary(rm => Fmt.MesTitulo(rm.Mes),
                    rm => rm.Top.Take(3).Select(x => $"{x.Rotulo}: {Fmt.Moeda(x.Valor)} ({Fmt.Pct(Pct(x.Valor, rm.TotalMes))} do mês)"))
            };
        }

        if (d.Despesa is { } s)
        {
            var maiorD = s.ConsolidadaPorMes.OrderByDescending(x => x.Value).FirstOrDefault();
            var menorD = s.ConsolidadaPorMes.OrderBy(x => x.Value).FirstOrDefault();
            despesa = new
            {
                consolidada = Fmt.Moeda(s.Consolidada),
                formula = $"Empenhado {Fmt.Moeda(s.Empenhado)} + Reforço {Fmt.Moeda(s.Reforco)} − Anulação {Fmt.Moeda(s.Anulacao)}",
                liquidado = $"{Fmt.Moeda(s.Liquidado)} ({Fmt.Pct(Pct(s.Liquidado, s.Consolidada))} da consolidada)",
                pago = $"{Fmt.Moeda(s.Pago)} ({Fmt.Pct(Pct(s.Pago, s.Liquidado))} do liquidado)",
                media_mensal = Fmt.Moeda(s.Consolidada / Math.Max(1, s.ConsolidadaPorMes.Count)),
                maior_mes = $"{Fmt.MesTitulo(maiorD.Key == 0 ? 1 : maiorD.Key)} ({Fmt.Moeda(maiorD.Value)})",
                menor_mes = $"{Fmt.MesTitulo(menorD.Key == 0 ? 1 : menorD.Key)} ({Fmt.Moeda(menorD.Value)})",
                por_mes = meses.Where(s.ConsolidadaPorMes.ContainsKey).ToDictionary(Fmt.MesTitulo, m => Fmt.Moeda(s.ConsolidadaPorMes[m])),
                por_modalidade = Linhas(s.PorModalidade, s.Consolidada),
                por_orgao = Linhas(s.PorOrgao, s.Consolidada),
                por_fonte = Linhas(s.PorFonte, s.Consolidada),
                por_funcao = s.PorFuncao.Select(x => $"{x.Rotulo}: {Fmt.Moeda(x.Valor)} ({Fmt.Pct(Pct(x.Valor, s.Consolidada))})"),
                quantidade_funcoes = s.PorFuncao.Count,
                quantidade_subfuncoes = s.PorSubfuncao.Count,
                principais_subfuncoes = s.PorSubfuncao.Take(8).Select(x => $"{x.Rotulo}: {Fmt.Moeda(x.Valor)} ({Fmt.Pct(Pct(x.Valor, s.Consolidada))})"),
                funcoes_detalhadas = s.FuncoesDetalhadas.ToDictionary(f => f.Funcao,
                    f => f.PorSubfuncao.Linhas.Take(4).Select(l => $"{l.Rotulo}: {Fmt.Moeda(l.Total)} ({Fmt.Pct(Pct(l.Total, f.PorSubfuncao.Total))} da função)"))
            };
        }

        if (d.Comparativo is { } c && d.Receita is { } ra && d.Despesa is { } da)
        {
            comparativo = new
            {
                ano_base = c.AnoBase,
                receita = Var(c.ReceitaBase, ra.Total),
                despesa_consolidada = Var(c.DespesaBase, da.Consolidada),
                despesa_paga = Var(c.PagoBase, da.Pago),
                resultado_base = $"Receita − Despesa = {Fmt.Moeda(c.ReceitaBase - c.DespesaBase)} ({Fmt.Pct(Pct(c.ReceitaBase - c.DespesaBase, c.ReceitaBase), 1)})",
                resultado_atual = $"Receita − Despesa = {Fmt.Moeda(ra.Total - da.Consolidada)} ({Fmt.Pct(Pct(ra.Total - da.Consolidada, ra.Total), 1)})",
                receita_por_fonte = c.ReceitaPorFonte.Select(x => $"{x.Rotulo}: {Var(x.Base, x.Atual)}"),
                receita_por_orgao = c.ReceitaPorOrgao.Select(x => $"{x.Rotulo}: {Var(x.Base, x.Atual)}"),
                pago_detalhado_por_subfuncao = c.PagoDetalhado.ToDictionary(f => f.Funcao,
                    f => f.Subfuncoes.Select(x => $"{x.Rotulo}: {Var(x.Base, x.Atual)}")),
                despesa_por_funcao = c.DespesaPorFuncao.Select(x => $"{x.Rotulo}: {Var(x.Base, x.Atual)}"),
                pago_por_funcao = c.PagoPorFuncao.Select(x => $"{x.Rotulo}: {Var(x.Base, x.Atual)}")
            };
        }

        return new
        {
            municipio = p.Municipio,
            exercicio = p.Ano,
            periodo = $"{Fmt.PeriodoExtenso(p.MesInicial, p.MesFinal)} de {p.Ano}",
            quantidade_meses = meses.Length,
            fonte_dos_dados = "Tribunal de Contas do Estado de São Paulo (TCESP)",
            receita,
            despesa,
            comparativo,
            avisos = d.Avisos
        };
    }

    static decimal? Pct(decimal parte, decimal total) => total == 0 ? null : parte / total * 100m;

    /// <summary>"RÓTULO: total (x% do total) — maior mês: Março (R$ ...)" para cada linha de uma tabela mensal.</summary>
    static IEnumerable<string> Linhas(MatrizMensal m, decimal total) => m.Linhas.Select(l =>
    {
        var pico = l.PorMes.OrderByDescending(x => x.Value).FirstOrDefault();
        return $"{l.Rotulo}: {Fmt.Moeda(l.Total)} ({Fmt.Pct(Pct(l.Total, total))})" +
               (l.PorMes.Count > 1 && pico.Key > 0 ? $" — maior mês: {Fmt.MesTitulo(pico.Key)} ({Fmt.Moeda(pico.Value)})" : "");
    });

    static string Var(decimal b, decimal a) =>
        $"{Fmt.Moeda(b)} → {Fmt.Moeda(a)} (diferença {Fmt.Moeda(a - b)}, {Fmt.Pct(b == 0 ? null : (a - b) / b * 100m)})";
}
