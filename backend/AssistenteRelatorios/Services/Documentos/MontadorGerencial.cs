using Microsoft.Extensions.Options;
using RelatorioIA.Api.Models;
using RelatorioIA.Api.Services.IA;

namespace RelatorioIA.Api.Services.Documentos;

public class RelatorioOptions
{
    public string Pasta { get; set; } = "relatorios";
    public string Consultoria { get; set; } = "GPN Consultoria";
    public string Responsavel { get; set; } = "";
    public string Contato { get; set; } = "";
    /// <summary>Cache dos brasões ({codigo}.png). Trocar um arquivo aqui corrige o brasão usado na capa.</summary>
    public string PastaBrasoes { get; set; } = "brasoes";
}

/// <summary>
/// Monta o RELATÓRIO GERENCIAL padrão (estrutura do modelo da consultoria) a partir dos números calculados
/// (<see cref="DadosRelatorio"/>) e dos textos da IA (<see cref="TextosRelatorio"/>).
/// </summary>
public class MontadorGerencial(IOptions<RelatorioOptions> opt)
{
    public Documento Montar(DadosRelatorio d, TextosRelatorio t, string? assinatura = null)
    {
        var p = d.Parametros;
        var periodo = $"{Fmt.PeriodoExtenso(p.MesInicial, p.MesFinal)} de {p.Ano}";
        var doc = new Documento { Titulo = "RELATÓRIO GERENCIAL", Municipio = p.Municipio, PeriodoAnalisado = periodo };

        doc.T1($"Análise Financeira do Município - {periodo}").Textos(t.Introducao);
        foreach (var a in d.Avisos) doc.P("Observação: " + a, italico: true);

        if (d.Receita is { } r) Receitas(doc, d, r, t, periodo);
        if (d.Despesa is { } s) Despesas(doc, d, s, t, periodo);
        if (d.Comparativo is { } c && d.Receita is { } ra && d.Despesa is { } da) Comparativo(doc, d, c, ra, da, t);

        doc.T1("Conclusão").Textos(t.Conclusao).P(ParagrafoQuantitativo);
        Encerramento(doc, opt.Value, p.Municipio, assinatura);
        return doc;
    }

    /// <summary>
    /// Parágrafo fixo da conclusão (exigência da gerência): escrito pelo sistema, sempre presente em todos os relatórios.
    /// </summary>
    public const string ParagrafoQuantitativo =
        "As análises apresentadas neste relatório são de caráter puramente quantitativo, elaboradas a partir dos dados " +
        "contábeis publicados pelo Tribunal de Contas do Estado de São Paulo (TCESP). Por isso, sugerem-se reuniões com os " +
        "gestores de cada área para avaliar os aspectos qualitativos dos gastos, como a efetividade das ações, a qualidade " +
        "dos serviços prestados e a aderência ao planejamento, de modo que as visões de cada área sejam compartilhadas e " +
        "sinergias sejam identificadas para se obter melhores resultados de forma conjunta.";

    /// <summary>Encerramento padrão (usado também nos relatórios por consulta).</summary>
    /// <param name="assinatura">Quem assina, pedido no chat (ex.: "Gestor responsável"); vazio = Relatorio:Responsavel ou a consultoria.</param>
    public static void Encerramento(Documento doc, RelatorioOptions o, string municipio, string? assinatura = null)
    {
        // Todo o bloco fica encadeado ("manter com o próximo") até o nome: a assinatura nunca vai sozinha para uma
        // página nova — sempre desce junto com o texto de encerramento e o último parágrafo da conclusão.
        if (doc.Blocos.Count > 0 && doc.Blocos[^1] is Paragrafo ultimo)
            doc.Blocos[^1] = ultimo with { ManterComProximo = true };
        doc.P($"Este relatório gerencial apresenta análises extraídas da base de dados do TCESP processada pela {o.Consultoria}. " +
              "É possível realizar diversas outras combinações e cruzamentos de dados, conforme as necessidades específicas de cada usuário.",
              manterComProximo: true);
        if (!string.IsNullOrWhiteSpace(o.Contato))
            doc.P($"Quaisquer dúvidas ou considerações podem ser encaminhadas para {o.Contato}.", manterComProximo: true);
        doc.P("", alin: Alinhamento.Direita, manterComProximo: true);
        var data = DateTime.Now.ToString("dd 'de' MMMM 'de' yyyy", Fmt.PtBr);
        // Comparativo entre cidades ("A × B"): só a data
        doc.P(municipio.Contains('×') ? $"{data}." : $"{municipio}, {data}.", alin: Alinhamento.Direita, manterComProximo: true);
        doc.P("", alin: Alinhamento.Centro, manterComProximo: true);
        doc.P("__________________________________________________", alin: Alinhamento.Centro, manterComProximo: true);
        var quem = !string.IsNullOrWhiteSpace(assinatura) ? assinatura
            : !string.IsNullOrWhiteSpace(o.Responsavel) ? o.Responsavel : o.Consultoria;
        doc.P(quem.ToUpper(Fmt.PtBr), negrito: true, alin: Alinhamento.Centro);
    }

    static void Receitas(Documento doc, DadosRelatorio d, DadosReceita r, TextosRelatorio t, string periodo)
    {
        var p = d.Parametros;
        var per = Fmt.Periodo(p.MesInicial, p.MesFinal);
        doc.T2($"Receita Consolidada do Município – {periodo}")
           .P("O valor de arrecadação das receitas consolidadas do município representa o total arrecadado a partir de " +
              "todas as fontes de receita pública municipal, oferecendo uma visão unificada da arrecadação, incluindo todos " +
              "os órgãos da administração municipal.")
           .Textos(t.AnaliseReceitas);

        doc.T3("Fontes de Arrecadação");
        TabelasMensais(doc, $"FONTES DE ARRECADAÇÃO - {per} - EXERCÍCIO {p.Ano}", "", r.PorFonte, "RECEITA TOTAL");
        doc.Textos(t.ComentarioFontes);
        Composicao(doc, $"ARRECADAÇÃO POR FONTE DE RECURSO - {per} - {p.Ano}", r.PorFonte, "DEMAIS FONTES");

        doc.T3("Arrecadação por Órgão");
        TabelasMensais(doc, $"ARRECADAÇÃO POR ÓRGÃO - {per} - EXERCÍCIO {p.Ano}", "", r.PorOrgao, "RECEITA TOTAL");
        doc.Textos(t.ComentarioOrgaosReceita);
        Composicao(doc, $"ARRECADAÇÃO POR ÓRGÃO - {per} - {p.Ano}", r.PorOrgao, "DEMAIS ÓRGÃOS");

        doc.T3("Arrecadação Consolidada Mensal");
        var linhas = d.Meses.Where(r.PorMes.ContainsKey).Select(m => new[] { Fmt.Mes(m), Fmt.Moeda(r.PorMes[m]) }).ToList();
        linhas.Add(["RECEITA TOTAL", Fmt.Moeda(r.Total)]);
        linhas.Add(["MÉDIA DA RECEITA", Fmt.Moeda(r.Total / Math.Max(1, r.PorMes.Count))]);
        doc.Tab(new TabelaDoc { Titulo = $"ARRECADAÇÃO POR MÊS - {per} - EXERCÍCIO {p.Ano}", Linhas = linhas, Larguras = [60, 40], NegritoUltimas = 2, CentralizarPrimeira = true, Fonte = 10 });
        doc.Textos(t.ComentarioReceitaMensal);
        Mensal(doc, $"ARRECADAÇÃO CONSOLIDADA POR MÊS - {per} - {p.Ano}", d.Meses, r.PorMes, "Receita consolidada");

        doc.T3("Categorias da Arrecadação");
        TabelasMensais(doc, $"CATEGORIAS DE ARRECADAÇÃO - {per} - EXERCÍCIO {p.Ano}", "", r.PorCategoria, "RECEITA TOTAL");
        doc.Textos(t.ComentarioCategorias);
        Composicao(doc, $"CATEGORIAS DE ARRECADAÇÃO - {per} - {p.Ano}", r.PorCategoria, "DEMAIS CATEGORIAS");

        doc.T3("Principais Rubricas na Arrecadação")
           .P("A rubrica de arrecadação é um código numérico utilizado para identificar e classificar, de maneira detalhada, " +
              "cada tipo de receita arrecadada pelo município. A seguir, as 6 (seis) maiores rubricas de cada mês, com o " +
              "percentual que cada uma representa sobre a receita consolidada do respectivo mês.");
        foreach (var rm in r.RubricasPorMes)
        {
            var parcial = rm.Top.Sum(x => x.Valor);
            var l = rm.Top.Select(x => new[] { x.Rotulo, Fmt.Moeda(x.Valor), Fmt.Pct(Pct(x.Valor, rm.TotalMes)) }).ToList();
            l.Add(["RECEITA PARCIAL", Fmt.Moeda(parcial), Fmt.Pct(Pct(parcial, rm.TotalMes))]);
            l.Add([$"RECEITA TOTAL DE {Fmt.Mes(rm.Mes)} DE {p.Ano}", Fmt.Moeda(rm.TotalMes), "100,00%"]);
            doc.Tab(new TabelaDoc
            {
                Titulo = $"RUBRICA DE ARRECADAÇÃO - {Fmt.Mes(rm.Mes)} - {p.Ano}", Cabecalho = ["", $"{Fmt.Mes(rm.Mes)}/{p.Ano}", "AV %"],
                Linhas = l, Larguras = [62, 25, 13], NegritoUltimas = 2, Fonte = 9.5
            });
        }
        doc.Textos(t.ComentarioRubricas);
        // Rubricas: a receita total de cada mês dividida entre as 6 maiores e as demais
        if (r.RubricasPorMes.Count > 0)
        {
            var meses = r.RubricasPorMes.OrderBy(x => x.Mes).ToList();
            doc.Grafico($"PRINCIPAIS RUBRICAS × DEMAIS RUBRICAS POR MÊS - {per} - {p.Ano}",
                new GraficoDados(TipoGrafico.Colunas, meses.Select(x => Fmt.MesCurto(x.Mes)).ToArray(),
                [
                    new SerieGrafico("6 maiores rubricas do mês", meses.Select(x => (double)x.Top.Sum(y => y.Valor)).ToArray()),
                    new SerieGrafico("Demais rubricas", meses.Select(x => (double)(x.TotalMes - x.Top.Sum(y => y.Valor))).ToArray())
                ]));
        }
    }

    // ------------------------------------------------------------------ gráficos de fim de módulo

    /// <summary>Parte do todo: pizza (rosca) com até 5 itens; acima disso, ranking em barras com o %.</summary>
    static void Composicao(Documento doc, string titulo, MatrizMensal m, string demais) =>
        Composicao(doc, titulo, m.Linhas.Select(l => (l.Rotulo, l.Total)), m.Total, demais);

    internal static void Composicao(Documento doc, string titulo, IEnumerable<(string rotulo, decimal valor)> itens, decimal total, string demais)
    {
        var lista = itens.Where(x => x.valor > 0).ToList();
        if (lista.Count < 2) return;
        doc.Grafico(titulo, lista.Count <= GeradorGraficos.MaxFatias
            ? Graficos.Rosca(lista, total, demais)
            : Graficos.Ranking(lista, total, 8, demais));
    }

    /// <summary>Evolução mensal de uma série.</summary>
    static void Mensal(Documento doc, string titulo, IReadOnlyList<int> meses, IReadOnlyDictionary<int, decimal> valores, string nome)
    {
        var ms = meses.Where(valores.ContainsKey).ToList();
        if (ms.Count < 2) return;
        doc.Grafico(titulo, new GraficoDados(TipoGrafico.Colunas, ms.Select(Fmt.MesCurto).ToArray(),
            [new SerieGrafico(nome, ms.Select(m => (double)valores[m]).ToArray())]));
    }

    /// <summary>Comparativo ano base × ano atual por item (as 8 maiores), em barras horizontais.</summary>
    static void GraficoComparativo(Documento doc, string titulo, List<ItemComparativo> itens, string rotBase, string rotAtual)
    {
        var top = itens.OrderByDescending(x => Math.Max(x.Base, x.Atual)).Take(8).ToList();
        if (top.Count < 2) return;
        doc.Grafico(titulo, new GraficoDados(TipoGrafico.Barras, top.Select(x => x.Rotulo).ToArray(),
        [
            new SerieGrafico(rotBase, top.Select(x => (double)x.Base).ToArray()),
            new SerieGrafico(rotAtual, top.Select(x => (double)x.Atual).ToArray())
        ]));
    }

    static void Despesas(Documento doc, DadosRelatorio d, DadosDespesa s, TextosRelatorio t, string periodo)
    {
        var p = d.Parametros;
        var per = Fmt.Periodo(p.MesInicial, p.MesFinal);
        doc.T2($"Despesa Empenhada Consolidada do Município – {periodo}")
           .P("A despesa empenhada consolidada representa o total de obrigações formalmente assumidas pelo município no " +
              "período. É composta pelos valores originalmente empenhados, acrescidos dos reforços e deduzidos das anulações:")
           .P("Despesa Empenhada Consolidada = Despesa Empenhada + Reforços – Anulações", negrito: true, alin: Alinhamento.Centro)
           .Textos(t.AnaliseDespesas);

        doc.T3("Despesa Empenhada por Modalidade");
        TabelasMensais(doc, $"MODALIDADE DE DESPESA - {per} - EXERCÍCIO {p.Ano}", "MODALIDADE DE DESPESA", s.PorModalidade, "TOTAL");
        doc.Textos(t.ComentarioModalidade);
        Composicao(doc, $"DESPESA EMPENHADA POR MODALIDADE - {per} - {p.Ano}", s.PorModalidade, "DEMAIS MODALIDADES");

        doc.T3("Tipos de Despesas")
           .P($"No período, o valor total das despesas liquidadas foi de {Fmt.Moeda(s.Liquidado)}, o que indica que " +
              $"{Fmt.Pct(Pct(s.Liquidado, s.Consolidada))} das despesas empenhadas consolidadas foram efetivamente liquidadas. " +
              $"As despesas pagas ({Fmt.Moeda(s.Pago)}) correspondem a {Fmt.Pct(Pct(s.Pago, s.Liquidado))} das despesas liquidadas.")
           .Tab(new TabelaDoc
           {
               Cabecalho = ["TIPOS DE DESPESAS", "VALOR"],
               Linhas =
               [
                   ["EMPENHADO", Fmt.Moeda(s.Empenhado)], ["REFORÇO", Fmt.Moeda(s.Reforco)], ["VALOR LIQUIDADO", Fmt.Moeda(s.Liquidado)],
                   ["VALOR PAGO", Fmt.Moeda(s.Pago)], ["ANULAÇÃO", Fmt.Moeda(-s.Anulacao)], ["DESPESA EMPENHADA CONSOLIDADA", Fmt.Moeda(s.Consolidada)]
               ],
               Larguras = [55, 45], NegritoUltimas = 1, CentralizarPrimeira = true, Fonte = 10
           })
           .Textos(t.ComentarioTiposDespesa);
        doc.Grafico($"EMPENHADO CONSOLIDADO × LIQUIDADO × PAGO - {per} - {p.Ano}",
            new GraficoDados(TipoGrafico.Colunas, ["Empenhada consolidada", "Liquidada", "Paga"],
                [new SerieGrafico("Valor", [(double)s.Consolidada, (double)s.Liquidado, (double)s.Pago])]));

        doc.T3("Despesa Empenhada Consolidada Mensal");
        var lm = d.Meses.Where(s.ConsolidadaPorMes.ContainsKey).Select(m => new[] { Fmt.Mes(m), Fmt.Moeda(s.ConsolidadaPorMes[m]) }).ToList();
        lm.Add(["TOTAL", Fmt.Moeda(s.Consolidada)]);
        lm.Add(["MÉDIA", Fmt.Moeda(s.Consolidada / Math.Max(1, s.ConsolidadaPorMes.Count))]);
        doc.Tab(new TabelaDoc { Titulo = "DESPESAS EMPENHADAS CONSOLIDADAS", Linhas = lm, Larguras = [60, 40], NegritoUltimas = 2, CentralizarPrimeira = true, Fonte = 10 });
        doc.Textos(t.ComentarioDespesaMensal);
        if (d.Receita is { } rec)
            doc.Grafico($"RECEITA CONSOLIDADA × DESPESA EMPENHADA CONSOLIDADA POR MÊS - {p.Ano}",
                new GraficoDados(TipoGrafico.Colunas, d.Meses.Select(Fmt.MesCurto).ToArray(),
                [
                    new SerieGrafico("Receita consolidada", d.Meses.Select(m => (double)rec.PorMes.GetValueOrDefault(m)).ToArray()),
                    new SerieGrafico("Despesa empenhada consolidada", d.Meses.Select(m => (double)s.ConsolidadaPorMes.GetValueOrDefault(m)).ToArray())
                ]));
        else Mensal(doc, $"DESPESA EMPENHADA CONSOLIDADA POR MÊS - {per} - {p.Ano}", d.Meses, s.ConsolidadaPorMes, "Despesa empenhada consolidada");

        doc.T3("Despesa Empenhada Consolidada por Órgão");
        TabelasMensais(doc, $"DESPESAS POR ÓRGÃO - {per} - EXERCÍCIO {p.Ano}", "DESPESAS POR ÓRGÃO", s.PorOrgao, "TOTAL");
        doc.Textos(t.ComentarioOrgaosDespesa);
        Composicao(doc, $"DESPESA EMPENHADA CONSOLIDADA POR ÓRGÃO - {per} - {p.Ano}", s.PorOrgao, "DEMAIS ÓRGÃOS");

        doc.T3("Despesa Empenhada Consolidada por Fonte de Recurso");
        TabelasMensais(doc, $"DESPESAS POR FONTE DE RECURSO - {per} - EXERCÍCIO {p.Ano}", "DESPESAS POR FONTE DE RECURSO", s.PorFonte, "TOTAL");
        doc.Textos(t.ComentarioFontesDespesa);
        Composicao(doc, $"DESPESA EMPENHADA CONSOLIDADA POR FONTE DE RECURSO - {per} - {p.Ano}", s.PorFonte, "DEMAIS FONTES");

        doc.T3("Despesa Empenhada Consolidada por Função e Subfunção do Governo")
           .P("A função indica o setor de atuação do município, enquanto a subfunção detalha essa aplicação, evidenciando o " +
              "objetivo específico da despesa dentro da respectiva área.");
        decimal acum = 0;
        var lf = s.PorFuncao.Select(x => { acum += x.Valor; return new[] { x.Rotulo, Fmt.Moeda(x.Valor), Fmt.Pct(Pct(x.Valor, s.Consolidada), 3), Fmt.Pct(Pct(acum, s.Consolidada), 3) }; }).ToList();
        lf.Add(["TOTAL", Fmt.Moeda(s.Consolidada), "100,000%", "100,000%"]);
        doc.Tab(new TabelaDoc
        {
            Titulo = $"DESPESAS EMPENHADAS CONSOLIDADAS POR FUNÇÃO - {per} - EXERCÍCIO {p.Ano}",
            Cabecalho = ["DESPESAS POR FUNÇÃO DO GOVERNO", "VALOR", "%", "% ACUMULADO"], Linhas = lf, Larguras = [46, 24, 15, 15], NegritoUltimas = 1
        });
        doc.Textos(t.ComentarioFuncoes);
        doc.Grafico($"PRINCIPAIS FUNÇÕES DE GOVERNO - DESPESA EMPENHADA CONSOLIDADA - {per} - {p.Ano}",
            Graficos.Ranking(s.PorFuncao.Select(x => (x.Rotulo, x.Valor)), s.Consolidada, 8, "DEMAIS FUNÇÕES"));

        var ls = s.PorSubfuncao.Select(x => new[] { x.Rotulo, Fmt.Moeda(x.Valor), Fmt.Pct(Pct(x.Valor, s.Consolidada), 3) }).ToList();
        ls.Add(["TOTAL", Fmt.Moeda(s.Consolidada), "100,000%"]);
        doc.Tab(new TabelaDoc
        {
            Titulo = $"DESPESAS EMPENHADAS CONSOLIDADAS POR SUBFUNÇÃO - {per} - EXERCÍCIO {p.Ano}",
            Cabecalho = ["DESPESAS POR SUBFUNÇÃO DO GOVERNO", "VALOR", "%"], Linhas = ls, Larguras = [56, 26, 18], NegritoUltimas = 1
        });
        doc.Textos(t.ComentarioSubfuncoes);
        Composicao(doc, $"PRINCIPAIS SUBFUNÇÕES - DESPESA EMPENHADA CONSOLIDADA - {per} - {p.Ano}",
            s.PorSubfuncao.Select(x => (x.Rotulo, x.Valor)), s.Consolidada, "DEMAIS SUBFUNÇÕES");

        doc.P($"As {s.FuncoesDetalhadas.Count} principais funções de governo são detalhadas a seguir por mês e subfunção:");
        foreach (var f in s.FuncoesDetalhadas)
            TabelasMensais(doc, $"FUNÇÃO {f.Funcao}", "SUBFUNÇÃO", f.PorSubfuncao, "TOTAL");
        doc.Textos(t.ComentarioFuncoesDetalhadas);
        foreach (var f in s.FuncoesDetalhadas)
            Composicao(doc, $"FUNÇÃO {f.Funcao} POR SUBFUNÇÃO - {per} - {p.Ano}", f.PorSubfuncao, "DEMAIS SUBFUNÇÕES");
    }

    static void Comparativo(Documento doc, DadosRelatorio d, DadosComparativo c, DadosReceita ra, DadosDespesa da, TextosRelatorio t)
    {
        var p = d.Parametros;
        var per = Fmt.PeriodoExtenso(p.MesInicial, p.MesFinal);
        string rot(int a) => $"{per.ToUpper(Fmt.PtBr)} /{a}";
        doc.T2($"Análise Comparativa dos Anos {c.AnoBase} e {p.Ano}")
           .P("Uma análise relevante das finanças públicas municipais consiste na comparação dos resultados entre exercícios: " +
              "a receita consolidada e a despesa empenhada consolidada do exercício atual com as do mesmo período do exercício anterior.");

        foreach (var (ano, rec, des) in new[] { (c.AnoBase, c.ReceitaBasePorMes, c.DespesaBasePorMes), (p.Ano, ra.PorMes, da.ConsolidadaPorMes) })
        {
            var l = d.Meses.Select(m =>
            {
                var rv = rec.GetValueOrDefault(m); var dv = des.GetValueOrDefault(m);
                return new[] { $"{Fmt.MesCurto(m)}/{ano}", Fmt.Moeda(rv), Fmt.Moeda(dv), Fmt.Moeda(rv - dv), Fmt.Pct(Pct(rv - dv, rv), 1) };
            }).ToList();
            var tr = rec.Values.Sum(); var td = des.Values.Sum();
            l.Add(["TOTAL", Fmt.Moeda(tr), Fmt.Moeda(td), Fmt.Moeda(tr - td), Fmt.Pct(Pct(tr - td, tr), 1)]);
            doc.Tab(new TabelaDoc
            {
                Cabecalho = ["EXERCÍCIO", "RECEITA CONSOLIDADA", "DESPESA EMPENHADA CONSOLIDADA", "DIFERENÇA", "% RECEITA VERSUS DESPESA"],
                Linhas = l, Larguras = [16, 22, 24, 22, 16], NegritoUltimas = 1, CentralizarPrimeira = true, ColunasVariacao = [3, 4]
            });
        }

        doc.Textos(t.AnaliseComparativa);
        doc.Grafico($"RECEITA E DESPESA EMPENHADA CONSOLIDADAS POR MÊS - {c.AnoBase} × {p.Ano}",
            new GraficoDados(TipoGrafico.Linha, d.Meses.Select(Fmt.MesCurto).ToArray(),
            [
                new SerieGrafico($"Receita {c.AnoBase}", d.Meses.Select(m => (double)c.ReceitaBasePorMes.GetValueOrDefault(m)).ToArray()),
                new SerieGrafico($"Receita {p.Ano}", d.Meses.Select(m => (double)ra.PorMes.GetValueOrDefault(m)).ToArray()),
                new SerieGrafico($"Despesa {c.AnoBase}", d.Meses.Select(m => (double)c.DespesaBasePorMes.GetValueOrDefault(m)).ToArray()),
                new SerieGrafico($"Despesa {p.Ano}", d.Meses.Select(m => (double)da.ConsolidadaPorMes.GetValueOrDefault(m)).ToArray())
            ]));
        doc.Tab(new TabelaDoc
        {
            Cabecalho = ["Exercício", "Receita Consolidada", "Despesa Empenhada Consolidada"],
            Linhas =
            [
                [rot(c.AnoBase), Fmt.Moeda(c.ReceitaBase), Fmt.Moeda(c.DespesaBase)],
                [rot(p.Ano), Fmt.Moeda(ra.Total), Fmt.Moeda(da.Consolidada)],
                ["Diferença", Fmt.Moeda(ra.Total - c.ReceitaBase), Fmt.Moeda(da.Consolidada - c.DespesaBase)],
                ["%", Fmt.Pct(Pct(ra.Total - c.ReceitaBase, c.ReceitaBase)), Fmt.Pct(Pct(da.Consolidada - c.DespesaBase, c.DespesaBase))]
            ],
            Larguras = [30, 35, 35], NegritoUltimas = 2, CentralizarPrimeira = true
        });
        doc.Grafico($"COMPARATIVO {per.ToUpper(Fmt.PtBr)} - {c.AnoBase} × {p.Ano}",
            new GraficoDados(TipoGrafico.Colunas, ["Receita consolidada", "Despesa empenhada consolidada", "Despesa paga"],
            [
                new SerieGrafico($"{per} {c.AnoBase}", [(double)c.ReceitaBase, (double)c.DespesaBase, (double)c.PagoBase]),
                new SerieGrafico($"{per} {p.Ano}", [(double)ra.Total, (double)da.Consolidada, (double)da.Pago])
            ]));

        Comparativa(doc, "FONTES DE ARRECADAÇÃO", rot(c.AnoBase), rot(p.Ano), c.ReceitaPorFonte, "RECEITA TOTAL");
        doc.Textos(t.ComentarioComparativoFontes);
        GraficoComparativo(doc, $"FONTES DE ARRECADAÇÃO - {c.AnoBase} × {p.Ano}", c.ReceitaPorFonte, $"{c.AnoBase}", $"{p.Ano}");
        Comparativa(doc, "ARRECADAÇÃO POR ÓRGÃO", rot(c.AnoBase), rot(p.Ano), c.ReceitaPorOrgao, "RECEITA TOTAL");
        doc.Textos(t.ComentarioComparativoOrgaos);
        GraficoComparativo(doc, $"ARRECADAÇÃO POR ÓRGÃO - {c.AnoBase} × {p.Ano}", c.ReceitaPorOrgao, $"{c.AnoBase}", $"{p.Ano}");
        Comparativa(doc, "FUNÇÃO DO GOVERNO", rot(c.AnoBase), rot(p.Ano), c.DespesaPorFuncao, "TOTAL", "DESPESA EMPENHADA CONSOLIDADA POR FUNÇÃO DE GOVERNO");
        doc.Textos(t.ComentarioComparativoFuncoes);
        GraficoComparativo(doc, $"DESPESA EMPENHADA CONSOLIDADA POR FUNÇÃO - {c.AnoBase} × {p.Ano}", c.DespesaPorFuncao, $"{c.AnoBase}", $"{p.Ano}");

        doc.P("As despesas empenhadas consolidadas representam os compromissos assumidos; as despesas pagas representam os " +
              "valores que efetivamente saíram dos cofres públicos. Por isso, a comparação considera também as despesas pagas:");
        doc.Tab(new TabelaDoc
        {
            Cabecalho = ["TIPOS DE DESPESAS", rot(c.AnoBase), rot(p.Ano), "DIFERENÇA", "%"],
            Linhas =
            [
                ["DESPESAS EMPENHADAS CONSOLIDADAS", Fmt.Moeda(c.DespesaBase), Fmt.Moeda(da.Consolidada), Fmt.Moeda(da.Consolidada - c.DespesaBase), Fmt.Pct(Pct(da.Consolidada - c.DespesaBase, c.DespesaBase))],
                ["DESPESAS PAGAS", Fmt.Moeda(c.PagoBase), Fmt.Moeda(da.Pago), Fmt.Moeda(da.Pago - c.PagoBase), Fmt.Pct(Pct(da.Pago - c.PagoBase, c.PagoBase))]
            ],
            Larguras = [30, 20, 20, 18, 12], ColunasVariacao = [3, 4]
        });

        Comparativa(doc, "FUNÇÃO DO GOVERNO", rot(c.AnoBase), rot(p.Ano), c.PagoPorFuncao, "TOTAL", $"DESPESAS PAGAS - {per.ToUpper(Fmt.PtBr)}");

        if (c.PagoDetalhado.Count > 0)
        {
            doc.P("Funções com as maiores variações nas despesas pagas, detalhadas por subfunção:");
            var l = new List<string[]>();
            var negritos = new HashSet<int>();
            foreach (var f in c.PagoDetalhado)
            {
                var primeira = true;
                foreach (var s in f.Subfuncoes)
                {
                    l.Add([primeira ? f.Funcao : "", s.Rotulo, Fmt.Moeda(s.Base), Fmt.Moeda(s.Atual), Fmt.Moeda(s.Diferenca), Fmt.Pct(s.Percentual)]);
                    primeira = false;
                }
                var tb = f.Subfuncoes.Sum(x => x.Base); var ta = f.Subfuncoes.Sum(x => x.Atual);
                negritos.Add(l.Count);
                l.Add(["", "TOTAL", Fmt.Moeda(tb), Fmt.Moeda(ta), Fmt.Moeda(ta - tb), Fmt.Pct(Pct(ta - tb, tb))]);
            }
            doc.Tab(new TabelaDoc
            {
                Titulo = $"DESPESAS PAGAS - {per.ToUpper(Fmt.PtBr)}", Cabecalho = ["FUNÇÃO", "SUBFUNÇÃO", rot(c.AnoBase), rot(p.Ano), "DIFERENÇA", "%"],
                Linhas = l, Larguras = LargurasAuto(l, 8, colunasTexto: 2), LinhasNegrito = negritos, Fonte = 8, ColunasVariacao = [4, 5],
                Nota = "Legenda: *** Não apresentou valores no exercício base para comparação."
            });
        }
        doc.Textos(t.ComentarioComparativoPagas);
        GraficoComparativo(doc, $"DESPESAS PAGAS POR FUNÇÃO - {c.AnoBase} × {p.Ano}", c.PagoPorFuncao, $"{c.AnoBase}", $"{p.Ano}");
    }

    const double FonteMensal = 8;
    const double MinPrimeiraColuna = 30;

    /// <summary>Largura (% da página) que uma coluna de valores precisa para não quebrar o maior texto.</summary>
    static double LarguraValor(IEnumerable<string> textos, double fonte) =>
        Math.Clamp(textos.Max(t => t.Length) * 0.12 * fonte + 3.2, 8, 28);

    /// <summary>
    /// Larguras calculadas pelo conteúdo: colunas de valor do tamanho do maior número (sem quebrar "R$ 19.254.904,71");
    /// as colunas de texto iniciais dividem o restante.
    /// </summary>
    public static int[] LargurasAuto(List<string[]> linhas, double fonte, int colunasTexto = 1, double minPorTexto = 22,
        string[]? cabecalho = null)
    {
        var n = linhas.Max(l => l.Length);
        var w = new double[n];
        for (int i = colunasTexto; i < n; i++)
        {
            // Considera também a maior PALAVRA do cabeçalho (o cabeçalho quebra entre palavras, nunca no meio; negrito ≈ +10%)
            var palavra = cabecalho is not null && i < cabecalho.Length
                ? new string('x', (int)Math.Ceiling(cabecalho[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Length).DefaultIfEmpty(0).Max() * 1.1))
                : "";
            w[i] = LarguraValor(linhas.Select(l => i < l.Length ? l[i] : "").Append(palavra), fonte);
        }
        var minTexto = minPorTexto * colunasTexto;
        var resto = 100 - w.Sum();
        if (resto < minTexto)
        {
            var f = (100 - minTexto) / w.Sum();
            for (int i = colunasTexto; i < n; i++) w[i] *= f;
            resto = minTexto;
        }
        var pesos = Enumerable.Range(0, colunasTexto).Select(i => (double)Math.Max(8, linhas.Max(l => l[i].Length))).ToArray();
        for (int i = 0; i < colunasTexto; i++) w[i] = resto * pesos[i] / pesos.Sum();
        var r = w.Select(x => (int)Math.Floor(x)).ToArray();
        r[0] += 100 - r.Sum();
        return r;
    }

    /// <summary>
    /// Tabela rótulo x mês + TOTAL + %, quebrada em blocos (como no modelo). O tamanho de cada bloco é calculado
    /// para que nenhum valor quebre de linha e a primeira coluna mantenha ao menos 30% da largura.
    /// </summary>
    public static void TabelasMensais(Documento doc, string titulo, string primeiraColuna, MatrizMensal m, string rotuloTotal)
    {
        var total = m.Total;
        var colunas = new List<(string cab, Func<LinhaMensal?, string> val)>();
        foreach (var mes in m.Meses) colunas.Add((Fmt.Mes(mes), l => Fmt.Moeda(l?.Mes(mes) ?? m.TotalMes(mes))));
        colunas.Add(("TOTAL", l => Fmt.Moeda(l?.Total ?? total)));
        colunas.Add(("%", l => Fmt.Pct(Pct(l?.Total ?? total, total))));

        // Largura de cada coluna de valor pelo maior texto (valores + total)
        var linhasFonte = m.Linhas.Cast<LinhaMensal?>().Append(null).ToList();
        var larguras = colunas.Select(c => LarguraValor(linhasFonte.Select(l => c.val(l)).Append(c.cab), FonteMensal)).ToList();

        // Blocos gulosos: acrescenta colunas enquanto couberem (máx. 5) mantendo a primeira coluna ≥ 30%
        var blocos = new List<List<int>>();
        var atual = new List<int>();
        for (int i = 0; i < colunas.Count; i++)
        {
            var soma = atual.Sum(j => larguras[j]) + larguras[i];
            if (atual.Count > 0 && (atual.Count >= 5 || soma > 100 - MinPrimeiraColuna)) { blocos.Add(atual); atual = []; }
            atual.Add(i);
        }
        if (atual.Count > 0) blocos.Add(atual);

        for (int b = 0; b < blocos.Count; b++)
        {
            var bloco = blocos[b];
            var linhas = m.Linhas.Select(l => bloco.Select(i => colunas[i].val(l)).Prepend(l.Rotulo).ToArray()).ToList();
            linhas.Add(bloco.Select(i => colunas[i].val(null)).Prepend(rotuloTotal).ToArray());
            var larg = bloco.Select(i => (int)Math.Ceiling(larguras[i])).Prepend(0).ToArray();
            larg[0] = 100 - larg.Sum();
            doc.Tab(new TabelaDoc
            {
                Titulo = b == 0 ? titulo : null, Cabecalho = bloco.Select(i => colunas[i].cab).Prepend(primeiraColuna).ToArray(),
                Linhas = linhas, Larguras = larg, NegritoUltimas = 1, Fonte = FonteMensal
            });
        }
    }

    static void Comparativa(Documento doc, string primeiraColuna, string rotBase, string rotAtual, List<ItemComparativo> itens, string rotuloTotal, string? titulo = null)
    {
        var l = itens.Select(x => new[] { x.Rotulo, Fmt.Moeda(x.Base), Fmt.Moeda(x.Atual), Fmt.Moeda(x.Diferenca), Fmt.Pct(x.Percentual) }).ToList();
        var tb = itens.Sum(x => x.Base); var ta = itens.Sum(x => x.Atual);
        l.Add([rotuloTotal, Fmt.Moeda(tb), Fmt.Moeda(ta), Fmt.Moeda(ta - tb), Fmt.Pct(Pct(ta - tb, tb))]);
        doc.Tab(new TabelaDoc
        {
            Titulo = titulo, Cabecalho = [primeiraColuna, rotBase, rotAtual, "DIFERENÇA", "%"], Linhas = l,
            Larguras = LargurasAuto(l, 8.5, cabecalho: [primeiraColuna, rotBase, rotAtual, "DIFERENÇA", "%"]), NegritoUltimas = 1,
            ColunasVariacao = [3, 4],
            Nota = itens.Any(x => x.Percentual is null) ? "Legenda: *** Não apresentou valores no exercício base para comparação." : null
        });
    }

    static decimal? Pct(decimal parte, decimal total) => total == 0 ? null : parte / total * 100m;
}
