using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RelatorioIA.Api.Models;

namespace RelatorioIA.Api.Services.IA;

/// <summary>
/// Modo sem IA: entende o pedido com regras simples e gera textos padrão a partir dos números.
/// Usado quando não há chave do Gemini ou quando a IA está fora do ar. Só atende o relatório gerencial
/// e perguntas de resumo (relatórios por consulta SQL exigem a IA).
/// </summary>
public partial class AssistenteLocal(MunicipiosConhecidos? conhecidos = null)
{
    public const string Nome = "Modo local (sem IA)";

    static readonly (string chave, int mes)[] Meses =
    [
        ("janeiro", 1), ("fevereiro", 2), ("marco", 3), ("abril", 4), ("maio", 5), ("junho", 6), ("julho", 7),
        ("agosto", 8), ("setembro", 9), ("outubro", 10), ("novembro", 11), ("dezembro", 12),
        ("jan", 1), ("fev", 2), ("mar", 3), ("abr", 4), ("mai", 5), ("jun", 6), ("jul", 7), ("ago", 8),
        ("set", 9), ("out", 10), ("nov", 11), ("dez", 12)
    ];

    public Plano Planejar(string mensagem, IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogo)
    {
        var t = SemAcento(mensagem.ToLowerInvariant());
        var i = new Plano();

        i.Municipio = catalogo.Select(c => c.Municipio).Distinct()
            .FirstOrDefault(m => t.Contains(SemAcento(m.ToLowerInvariant())));

        var anos = RegexAno().Matches(t).Select(m => int.Parse(m.Value)).ToList();
        var anoMax = catalogo.Count > 0 ? catalogo.Max(c => c.Ano) : DateTime.Now.Year;
        if (t.Contains("este ano") || t.Contains("deste ano") || t.Contains("ano atual")) anos.Add(DateTime.Now.Year);
        if (t.Contains("ano passado") || t.Contains("ano anterior")) anos.Add(DateTime.Now.Year - 1);
        if (anos.Count > 0) i.Ano = anos.Max();
        if (anos.Distinct().Count() > 1) i.AnoComparacao = anos.Min();
        else if (t.Contains("compar")) i.AnoComparacao = (i.Ano ?? anoMax) - 1;

        var mesesCitados = RegexPalavra().Matches(t).Select(m => m.Value)
            .Select(w => Meses.FirstOrDefault(x => x.chave == w).mes).Where(m => m > 0).ToList();
        if (mesesCitados.Count > 0)
        {
            i.MesInicial = mesesCitados.Min();
            i.MesFinal = mesesCitados.Max();
            if (mesesCitados.Count == 1 && (t.Contains("ate ") || t.Contains("acumulado"))) i.MesInicial = 1;
        }

        var periodo = RegexPeriodo().Match(t);
        if (periodo.Success)
        {
            var n = periodo.Groups[1].Value switch
            {
                "primeiro" => 1, "segundo" => 2, "terceiro" => 3, "quarto" => 4, "quinto" => 5, "sexto" => 6,
                var x => int.Parse(x)
            };
            var tam = periodo.Groups[2].Value switch { "bimestre" => 2, "trimestre" => 3, "quadrimestre" => 4, _ => 6 };
            if (n >= 1 && n * tam <= 12) { i.MesInicial = (n - 1) * tam + 1; i.MesFinal = n * tam; }
        }

        var soReceita = t.Contains("receita") || t.Contains("arrecada");
        var soDespesa = t.Contains("despesa") || t.Contains("gasto") || t.Contains("empenh");
        if (soReceita ^ soDespesa) { i.IncluirReceitas = soReceita; i.IncluirDespesas = soDespesa; }

        // Cidades citadas que não estão no banco (lista de municípios da UF, quando disponível)
        var municipiosBanco = catalogo.Select(c => c.Municipio).Distinct().ToList();
        var foraDoBanco = conhecidos is null ? [] : MunicipiosConhecidos.CitadosForaDoBanco(mensagem, conhecidos.Nomes(), municipiosBanco);
        if (foraDoBanco.Count > 0)
        {
            var lista = string.Join(" e ", foraDoBanco);
            var disponiveis = string.Join(", ", municipiosBanco);
            if (i.Municipio is null)
                return new Plano
                {
                    Acao = "esclarecer",
                    Mensagem = $"{lista} {(foraDoBanco.Count > 1 ? "ainda não estão disponíveis" : "ainda não está disponível")} na base de dados. " +
                               $"Hoje {(municipiosBanco.Count > 1 ? "estão disponíveis" : "está disponível")}: {disponiveis}."
                };
            i.Mensagem = $"{lista} {(foraDoBanco.Count > 1 ? "não estão disponíveis" : "não está disponível")} na base de dados; " +
                         $"o relatório foi gerado só para {i.Municipio}.";
        }

        // Comparativo entre municípios exige a IA (consultas SQL): gera o gerencial do 1º citado e avisa
        var citadosNoBanco = municipiosBanco.Where(m => t.Contains(SemAcento(m.ToLowerInvariant()))).ToList();
        if (citadosNoBanco.Count > 1)
            i.Mensagem = $"A IA está temporariamente indisponível e o comparativo entre {string.Join(" e ", citadosNoBanco)} " +
                         $"depende dela. Foi gerado o relatório gerencial de {i.Municipio}; tente o comparativo novamente em alguns minutos.";

        // Sem IA, na dúvida gera o relatório (é o que o usuário quer na maioria dos pedidos). Resumo no chat só para
        // perguntas diretas ("quanto...?", "qual...?").
        var pergunta = mensagem.TrimEnd().EndsWith('?') || RegexPergunta().IsMatch(t);
        var pedeRelatorio = t.Contains("relatorio") || t.Contains("gerar") || t.Contains("gere") || t.Contains("monte") ||
                            t.Contains("analise") || t.Contains("documento") || t.Contains("compar") || t.Contains("levantamento") ||
                            t.Contains("panorama") || t.Contains("visao geral") || t.Contains("preciso") || t.Contains("quero") ||
                            t.Contains("gostaria") || foraDoBanco.Count > 0;
        var assuntoFinanceiro = soReceita || soDespesa || t.Contains("pago") || t.Contains("liquidad") ||
                                t.Contains("orcament") || t.Contains("financ") || t.Contains("resultado") ||
                                RegexReceitaConhecida().IsMatch(t);
        if (pedeRelatorio || (assuntoFinanceiro && !pergunta))
        {
            i.Acao = "relatorio_gerencial";
            if (i.AnoComparacao is null && t.Contains("gerencial")) i.AnoComparacao = (i.Ano ?? anoMax) - 1;
        }
        else if (assuntoFinanceiro) i.Acao = "responder";
        else
        {
            i.Acao = "esclarecer";
            i.Mensagem = "Posso ajudar com relatórios e perguntas sobre receitas e despesas do município (dados do TCESP). " +
                         "Exemplo: \"Relatório gerencial de janeiro a maio de 2026 comparando com 2025\".";
        }
        return i;
    }

    public TextosRelatorio RedigirGerencial(DadosRelatorio d)
    {
        var p = d.Parametros;
        var periodo = $"{Fmt.PeriodoExtenso(p.MesInicial, p.MesFinal).ToLower(Fmt.PtBr)} de {p.Ano}";
        var t = new TextosRelatorio
        {
            Introducao =
                $"Os dados do exercício de {p.Ano} disponibilizados pelo Tribunal de Contas do Estado de São Paulo (TCESP), " +
                $"do município de {p.Municipio}, referentes a {periodo}, são analisados neste relatório sob diferentes perspectivas.\n\n" +
                "As informações são apresentadas de forma segregada, considerando as receitas e despesas municipais, de modo a " +
                "facilitar a avaliação e a tomada de decisão pelos gestores públicos."
        };

        if (d.Receita is { } r)
        {
            var maior = r.PorMes.OrderByDescending(x => x.Value).First();
            var fonte = r.PorFonte.Linhas.FirstOrDefault();
            t.AnaliseReceitas =
                $"No período de {periodo}, a receita consolidada do município totalizou {Fmt.Moeda(r.Total)}, com média mensal de " +
                $"{Fmt.Moeda(r.Total / r.PorMes.Count)}. O mês de maior arrecadação foi {Fmt.MesTitulo(maior.Key).ToLower(Fmt.PtBr)}, com {Fmt.Moeda(maior.Value)}." +
                (fonte is null ? "" : $"\n\nA principal fonte de arrecadação foi {fonte.Rotulo}, com {Fmt.Moeda(fonte.Total)} " +
                                      $"({Fmt.Pct(fonte.Total / r.Total * 100)} do total).");
        }

        if (d.Despesa is { } s)
        {
            var func = s.PorFuncao.Take(3).Select(x => x.Rotulo.ToLower(Fmt.PtBr)).ToList();
            t.AnaliseDespesas =
                $"A despesa empenhada consolidada do período somou {Fmt.Moeda(s.Consolidada)}, correspondente a {Fmt.Moeda(s.Empenhado)} " +
                $"de despesas empenhadas, mais {Fmt.Moeda(s.Reforco)} de reforço, menos {Fmt.Moeda(s.Anulacao)} de anulação.\n\n" +
                $"O valor liquidado foi de {Fmt.Moeda(s.Liquidado)} ({Fmt.Pct(s.Consolidada == 0 ? null : s.Liquidado / s.Consolidada * 100)} da despesa consolidada) " +
                $"e o valor pago de {Fmt.Moeda(s.Pago)} ({Fmt.Pct(s.Liquidado == 0 ? null : s.Pago / s.Liquidado * 100)} do liquidado). " +
                (func.Count > 0 ? $"As funções de governo com maior volume de despesas foram {string.Join(", ", func)}." : "");
        }

        if (d.Comparativo is { } c && d.Receita is { } ra && d.Despesa is { } da)
        {
            t.AnaliseComparativa =
                $"No mesmo período de {c.AnoBase}, o resultado entre receita consolidada e despesa empenhada consolidada foi de {Fmt.Moeda(c.ReceitaBase - c.DespesaBase)}; " +
                $"em {p.Ano}, de {Fmt.Moeda(ra.Total - da.Consolidada)}.\n\n" +
                $"A receita variou de {Fmt.Moeda(c.ReceitaBase)} para {Fmt.Moeda(ra.Total)} ({Fmt.Pct(c.ReceitaBase == 0 ? null : (ra.Total - c.ReceitaBase) / c.ReceitaBase * 100)}), " +
                $"enquanto a despesa empenhada consolidada variou de {Fmt.Moeda(c.DespesaBase)} para {Fmt.Moeda(da.Consolidada)} " +
                $"({Fmt.Pct(c.DespesaBase == 0 ? null : (da.Consolidada - c.DespesaBase) / c.DespesaBase * 100)}) e a despesa paga de " +
                $"{Fmt.Moeda(c.PagoBase)} para {Fmt.Moeda(da.Pago)} ({Fmt.Pct(c.PagoBase == 0 ? null : (da.Pago - c.PagoBase) / c.PagoBase * 100)}).";
        }

        t.Conclusao =
            "Com base nos registros do período, foi desenvolvida uma análise financeira de natureza quantitativa, que permite " +
            "mensurar as variações observadas e fornece elementos objetivos para o acompanhamento da execução orçamentária.\n\n" +
            "Recomenda-se que os indicadores de despesas empenhadas consolidadas sejam analisados em conjunto com as despesas " +
            "pagas e com critérios qualitativos, proporcionando uma visão mais completa da aplicação dos recursos públicos.";
        PreencherComentarios(d, t);
        return t;
    }

    public string ResponderResumo(DadosRelatorio d)
    {
        var p = d.Parametros;
        var sb = new StringBuilder($"Resumo de {p.Municipio}, {Fmt.PeriodoExtenso(p.MesInicial, p.MesFinal).ToLower(Fmt.PtBr)} de {p.Ano}:\n");
        if (d.Receita is { } r) sb.AppendLine($"• Receita consolidada: {Fmt.Moeda(r.Total)}");
        if (d.Despesa is { } s)
        {
            sb.AppendLine($"• Despesa empenhada consolidada: {Fmt.Moeda(s.Consolidada)}");
            sb.AppendLine($"• Liquidado: {Fmt.Moeda(s.Liquidado)} · Pago: {Fmt.Moeda(s.Pago)}");
        }
        sb.Append("Quer que eu gere o relatório completo em Word/PDF?");
        return sb.ToString();
    }

    static string SemAcento(string s)
    {
        var n = s.Normalize(NormalizationForm.FormD);
        return new string(n.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    [GeneratedRegex(@"^\s*(quanto|quantos|quantas|qual|quais|como|quando|onde|houve|existe)\b")] private static partial Regex RegexPergunta();
    [GeneratedRegex(@"\b(fpm|icms|ipva|iptu|iss|itbi|fundeb|sus|folha|pessoal|saude|educacao)\b")] private static partial Regex RegexReceitaConhecida();
    [GeneratedRegex(@"\b20\d{2}\b")] private static partial Regex RegexAno();
    [GeneratedRegex(@"\p{L}+")] private static partial Regex RegexPalavra();
    [GeneratedRegex(@"\b([1-6]|primeiro|segundo|terceiro|quarto|quinto|sexto)\s*[ºo°]?\s*(bimestre|trimestre|quadrimestre|semestre)\b")]
    private static partial Regex RegexPeriodo();
}
