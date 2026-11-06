using RelatorioIA.Api.Models;

namespace RelatorioIA.Api.Services.IA;

/// <summary>
/// Comentários padrão (sem IA) para cada quadro do relatório gerencial, escritos com os números calculados.
/// Garantem que NENHUM quadro fique sem comentário: são usados no modo local e para completar qualquer campo
/// que a IA devolva vazio.
/// </summary>
public partial class AssistenteLocal
{
    /// <summary>Preenche apenas os comentários que estiverem vazios.</summary>
    public static void PreencherComentarios(DadosRelatorio d, TextosRelatorio t)
    {
        static void Se(Func<string> get, Action<string> set, Func<string> gerar)
        {
            if (string.IsNullOrWhiteSpace(get())) set(gerar());
        }

        if (d.Receita is { } r)
        {
            Se(() => t.ComentarioFontes, v => t.ComentarioFontes = v,
                () => Participacao(r.PorFonte, r.Total, "a fonte", "da arrecadação do período"));
            Se(() => t.ComentarioOrgaosReceita, v => t.ComentarioOrgaosReceita = v,
                () => Participacao(r.PorOrgao, r.Total, "o órgão", "da receita consolidada"));
            Se(() => t.ComentarioReceitaMensal, v => t.ComentarioReceitaMensal = v,
                () => Mensal(r.PorMes, "arrecadação", "receita"));
            Se(() => t.ComentarioCategorias, v => t.ComentarioCategorias = v,
                () => Participacao(r.PorCategoria, r.Total, "a categoria", "da receita consolidada"));
            Se(() => t.ComentarioRubricas, v => t.ComentarioRubricas = v, () =>
            {
                var top = r.PrincipaisRubricasPeriodo.Take(2).ToList();
                if (top.Count == 0) return "";
                var soma = top.Sum(x => x.Valor);
                return $"Considerando todo o período, as principais rubricas de arrecadação foram {string.Join(" e ", top.Select(x => $"{x.Rotulo} ({Fmt.Moeda(x.Valor)})"))}, " +
                       $"que, somadas, correspondem a {Fmt.Pct(Pct(soma, r.Total))} da receita consolidada.";
            });
        }

        if (d.Despesa is { } s)
        {
            Se(() => t.ComentarioModalidade, v => t.ComentarioModalidade = v,
                () => Participacao(s.PorModalidade, s.Consolidada, "a modalidade", "da despesa empenhada consolidada"));
            Se(() => t.ComentarioTiposDespesa, v => t.ComentarioTiposDespesa = v, () =>
                $"Do total liquidado de {Fmt.Moeda(s.Liquidado)}, foram pagos {Fmt.Moeda(s.Pago)} ({Fmt.Pct(Pct(s.Pago, s.Liquidado))}). " +
                $"A diferença de {Fmt.Moeda(s.Liquidado - s.Pago)} corresponde a obrigações já liquidadas e ainda não pagas, que, se não " +
                "quitadas no exercício, tornam-se restos a pagar.");
            Se(() => t.ComentarioDespesaMensal, v => t.ComentarioDespesaMensal = v, () =>
            {
                var texto = Mensal(s.ConsolidadaPorMes, "despesa empenhada consolidada", "despesa");
                if (d.Receita is { } rec)
                {
                    var acima = s.ConsolidadaPorMes.Where(x => x.Value > rec.PorMes.GetValueOrDefault(x.Key)).Select(x => Fmt.MesTitulo(x.Key).ToLower(Fmt.PtBr)).ToList();
                    texto += acima.Count == 0
                        ? " Em todos os meses do período a receita consolidada superou a despesa empenhada consolidada."
                        : $" A despesa empenhada consolidada superou a receita consolidada em {Lista(acima)}, o que merece acompanhamento.";
                }
                return texto;
            });
            Se(() => t.ComentarioOrgaosDespesa, v => t.ComentarioOrgaosDespesa = v,
                () => Participacao(s.PorOrgao, s.Consolidada, "o órgão", "da despesa empenhada consolidada"));
            Se(() => t.ComentarioFontesDespesa, v => t.ComentarioFontesDespesa = v,
                () => Participacao(s.PorFonte, s.Consolidada, "a fonte de recurso", "da despesa empenhada consolidada"));
            Se(() => t.ComentarioFuncoes, v => t.ComentarioFuncoes = v, () =>
            {
                var top5 = s.PorFuncao.Take(5).ToList();
                if (top5.Count == 0) return "";
                return $"Observa-se que as {top5.Count} primeiras funções de governo ({Lista(top5.Select(x => x.Rotulo.ToLower(Fmt.PtBr)).ToList())}) " +
                       $"representam {Fmt.Pct(Pct(top5.Sum(x => x.Valor), s.Consolidada))} da despesa empenhada consolidada, com destaque para " +
                       $"{top5[0].Rotulo.ToLower(Fmt.PtBr)}, que concentra {Fmt.Moeda(top5[0].Valor)} ({Fmt.Pct(Pct(top5[0].Valor, s.Consolidada))}).";
            });
            Se(() => t.ComentarioSubfuncoes, v => t.ComentarioSubfuncoes = v, () =>
            {
                var top = s.PorSubfuncao.Take(3).ToList();
                if (top.Count == 0) return "";
                return $"No período, as despesas foram classificadas em {s.PorSubfuncao.Count} subfunções. As de maior peso foram " +
                       $"{Lista(top.Select(x => $"{x.Rotulo.ToLower(Fmt.PtBr)} ({Fmt.Pct(Pct(x.Valor, s.Consolidada))})").ToList())}.";
            });
            Se(() => t.ComentarioFuncoesDetalhadas, v => t.ComentarioFuncoesDetalhadas = v, () =>
                string.Join(" ", s.FuncoesDetalhadas.Select(f =>
                {
                    var principal = f.PorSubfuncao.Linhas.FirstOrDefault();
                    return principal is null ? "" :
                        $"Na função {f.Funcao.ToLower(Fmt.PtBr)}, a subfunção {principal.Rotulo.ToLower(Fmt.PtBr)} responde por " +
                        $"{Fmt.Pct(Pct(principal.Total, f.PorSubfuncao.Total))} do total da função ({Fmt.Moeda(principal.Total)}).";
                })));
        }

        if (d.Comparativo is { } c && d.Receita is { } ra && d.Despesa is { } da)
        {
            Se(() => t.ComentarioComparativoFontes, v => t.ComentarioComparativoFontes = v,
                () => Variacoes(c.ReceitaPorFonte, "fonte", "a arrecadação"));
            Se(() => t.ComentarioComparativoOrgaos, v => t.ComentarioComparativoOrgaos = v,
                () => Variacoes(c.ReceitaPorOrgao, "órgão", "a arrecadação"));
            Se(() => t.ComentarioComparativoFuncoes, v => t.ComentarioComparativoFuncoes = v,
                () => Variacoes(c.DespesaPorFuncao, "função", "a despesa empenhada consolidada"));
            Se(() => t.ComentarioComparativoPagas, v => t.ComentarioComparativoPagas = v, () =>
                $"Enquanto a despesa empenhada consolidada variou {Fmt.Pct(Pct(da.Consolidada - c.DespesaBase, c.DespesaBase))} entre " +
                $"{c.AnoBase} e {d.Parametros.Ano}, a despesa paga variou {Fmt.Pct(Pct(da.Pago - c.PagoBase, c.PagoBase))} " +
                $"({Fmt.Moeda(c.PagoBase)} para {Fmt.Moeda(da.Pago)}). " + Variacoes(c.PagoPorFuncao, "função", "a despesa paga"));
        }
    }

    /// <summary>"Como pode ser verificado no quadro acima, X respondeu por Y% (R$) ..., seguido de ..."</summary>
    static string Participacao(MatrizMensal m, decimal total, string tipo, string complemento)
    {
        var l = m.Linhas.OrderByDescending(x => x.Total).ToList();
        if (l.Count == 0 || total == 0) return "";
        if (l.Count == 1)
            return $"Como pode ser verificado no quadro acima, {tipo} {l[0].Rotulo} concentrou a totalidade {complemento}, " +
                   $"com {Fmt.Moeda(l[0].Total)}.";
        var texto = $"Como pode ser verificado no quadro acima, {tipo} {l[0].Rotulo} respondeu por {Fmt.Pct(Pct(l[0].Total, total))} " +
                    $"{complemento} ({Fmt.Moeda(l[0].Total)}), seguido(a) de {l[1].Rotulo}, com {Fmt.Pct(Pct(l[1].Total, total))} " +
                    $"({Fmt.Moeda(l[1].Total)}).";
        if (l.Count > 2)
            texto += $" Juntos, os dois primeiros itens somam {Fmt.Pct(Pct(l[0].Total + l[1].Total, total))} do total.";
        return texto;
    }

    static string Mensal(Dictionary<int, decimal> porMes, string nome, string curto)
    {
        if (porMes.Count == 0) return "";
        var maior = porMes.MaxBy(x => x.Value); var menor = porMes.MinBy(x => x.Value);
        var media = porMes.Values.Sum() / porMes.Count;
        return $"Ao analisar a {nome} mês a mês, verifica-se uma média mensal de {Fmt.Moeda(media)}. O maior valor ocorreu em " +
               $"{Fmt.MesTitulo(maior.Key).ToLower(Fmt.PtBr)} ({Fmt.Moeda(maior.Value)}) e o menor em " +
               $"{Fmt.MesTitulo(menor.Key).ToLower(Fmt.PtBr)} ({Fmt.Moeda(menor.Value)}).";
    }

    static string Variacoes(List<ItemComparativo> itens, string tipo, string nome)
    {
        var comBase = itens.Where(x => x.Base != 0).ToList();
        var alta = comBase.Where(x => x.Diferenca > 0).OrderByDescending(x => x.Diferenca).FirstOrDefault();
        var queda = comBase.Where(x => x.Diferenca < 0).OrderBy(x => x.Diferenca).FirstOrDefault();
        var partes = new List<string>();
        if (alta != null) partes.Add($"o maior acréscimo em {nome} ocorreu na {tipo} {alta.Rotulo.ToLower(Fmt.PtBr)}, de {Fmt.Moeda(alta.Diferenca)} ({Fmt.Pct(alta.Percentual)})");
        if (queda != null) partes.Add($"a maior redução, na {tipo} {queda.Rotulo.ToLower(Fmt.PtBr)}, de {Fmt.Moeda(queda.Diferenca)} ({Fmt.Pct(queda.Percentual)})");
        var novos = itens.Where(x => x.Base == 0 && x.Atual != 0).Select(x => x.Rotulo.ToLower(Fmt.PtBr)).ToList();
        var texto = partes.Count == 0 ? "" : "Na comparação entre os exercícios, " + string.Join("; ", partes) + ".";
        if (novos.Count > 0) texto += $" Não apresentaram valores no exercício anterior: {Lista(novos)}.";
        return texto.Trim();
    }

    static string Lista(List<string> itens) => itens.Count switch
    {
        0 => "",
        1 => itens[0],
        _ => string.Join(", ", itens.Take(itens.Count - 1)) + " e " + itens[^1]
    };

    static decimal? Pct(decimal parte, decimal total) => total == 0 ? null : parte / total * 100m;
}
