using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Models;
using RelatorioIA.Api.Services.Consultas;
using RelatorioIA.Api.Services.IA;

namespace RelatorioIA.Api.Services.Documentos;

/// <summary>
/// Monta o documento padrão a partir da estrutura escrita pela IA (Prompt 2) + resultados reais do SQL.
/// As tabelas são preenchidas AQUI com os valores do banco — a IA só escolhe colunas e escreve os textos.
/// </summary>
public partial class MontadorConsulta(IOptions<RelatorioOptions> opt)
{
    const int MaxLinhasTabela = 40;

    /// <param name="notaConclusao">Nota curta de método (ex.: projeção), exibida discretamente na conclusão.</param>
    /// <param name="anexos">Arquivos anexados ao pedido: os números do texto deles também contam como "conferidos".</param>
    public (Documento doc, List<string> naoConferidos) Montar(EstruturaRelatorio e, Plano plano,
        IReadOnlyList<ResultadoConsulta> resultados, string municipio, IEnumerable<string> avisos, string? notaConclusao = null,
        IReadOnlyList<AnexoConteudo>? anexos = null)
    {
        var doc = new Documento
        {
            Titulo = municipio.Contains('×') ? "RELATÓRIO COMPARATIVO" : "RELATÓRIO GERENCIAL",
            Municipio = municipio,
            Subtitulo = string.IsNullOrWhiteSpace(e.Subtitulo) ? e.Titulo : e.Subtitulo,
            PeriodoAnalisado = plano.PeriodoDescricao ?? ""
        };

        var titulo = string.IsNullOrWhiteSpace(e.Titulo) ? plano.Titulo ?? "Análise" : e.Titulo;
        doc.T1(string.IsNullOrWhiteSpace(plano.PeriodoDescricao) ? titulo : $"{titulo} - {plano.PeriodoDescricao}");
        doc.Textos(e.Introducao);
        foreach (var a in avisos) doc.P("Observação: " + a, italico: true);

        var porId = resultados.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

        // Projeção: a tabela-resumo anual é SEMPRE inserida pelo sistema, com todas as colunas
        // (inclusive meses sem dados publicados — para o % realizado não ser lido como sobre o ano inteiro)
        if (porId.TryGetValue("projecao_anual", out var pa) && pa.Ok && pa.Linhas.Count > 0)
        {
            string M(object? v) => v is null ? "" : Fmt.Moeda(ExecutorSql.ParaDecimal(v));
            // Margem de erro (95%) em R$ e em % — como em pesquisas; anos só com dados realizados não têm margem
            bool SemMargem(object? me) => me is null || ExecutorSql.ParaDecimal(me) == 0;
            var linhasResumo = pa.Linhas.Select(l => new[]
            {
                Convert.ToString(l[0]) ?? "",
                M(l[3]), M(l[4]), M(l[5]),
                SemMargem(l.ElementAtOrDefault(7)) ? "—" : "± " + M(l[7]),
                SemMargem(l.ElementAtOrDefault(7)) || l.ElementAtOrDefault(8) is null ? "—" : "± " + Fmt.Pct(ExecutorSql.ParaDecimal(l[8])),
                l[6] is null ? "***" : Fmt.Pct(ExecutorSql.ParaDecimal(l[6]))
            }).ToList();
            string[] cabResumo = ["ANO", "REALIZADO", "ESTIMATIVA (TENDÊNCIA)", "ESTIMATIVA (ESTÁVEL)", "MARGEM DE ERRO (95%)", "MARGEM (%)", "% REALIZADO"];
            // Meses realizados / sem dados: em nota, para a tabela caber sem quebrar os valores
            var meses = string.Join("; ", pa.Linhas.Select(l =>
                $"{l[0]}: {l[1]} meses realizados" + (ExecutorSql.ParaDecimal(l[2]) > 0 ? $", {l[2]} sem dados publicados" : "")));
            doc.Tab(new TabelaDoc
            {
                Titulo = "RESUMO DA PROJEÇÃO POR ANO (REALIZADO × ESTIMADO)",
                Cabecalho = cabResumo,
                Linhas = linhasResumo,
                Larguras = MontadorGerencial.LargurasAuto(linhasResumo, 7.5, minPorTexto: 7, cabecalho: cabResumo),
                CentralizarPrimeira = true,
                Fonte = 7.5,
                Nota = $"{meses}. Meses já passados sem dados completos publicados não são estimados."
            });
        }

        // Estudo estatístico (média da amostra, desvio padrão, margem de erro) — inserido pelo sistema
        if (porId.TryGetValue("projecao_estatistica", out var pe) && pe.Ok && pe.Linhas.Count > 0)
            doc.Tab(new TabelaDoc
            {
                Titulo = "ESTUDO ESTATÍSTICO DA PROJEÇÃO",
                Cabecalho = ["INDICADOR", "VALOR"],
                Linhas = pe.Linhas.Select(l => new[] { Convert.ToString(l[0]) ?? "", Convert.ToString(l[1]) ?? "" }).ToList(),
                Larguras = [70, 30],
                Fonte = 9,
                Nota = "Assim como nas pesquisas de opinião, a margem de erro indica a faixa em que o resultado deve ficar, " +
                       "com 95% de confiança, considerando a variação observada mês a mês entre os anos."
            });
        var numerosDisponiveis = new HashSet<string>();
        foreach (var r in resultados.Where(r => r.Ok)) RegistrarNumeros(r, numerosDisponiveis);
        // Números que aparecem no texto dos anexos (Word/Excel/CSV) também podem ser citados
        foreach (var a in anexos ?? [])
            if (a.Texto is { } txt)
                foreach (System.Text.RegularExpressions.Match m in RegexNumeroBr().Matches(txt)) numerosDisponiveis.Add(m.Value);
        var valoresTabelaAnexo = new List<string>();
        void Registrar(TabelaDoc tab)
        {
            foreach (var linha in tab.Linhas) foreach (var c in linha) numerosDisponiveis.Add(c.Replace("R$", "").Trim(' ', '(', ')', '%'));
        }

        foreach (var s in e.Secoes)
        {
            doc.T2(s.Titulo).Textos(s.TextoAntes);
            if (s.Tabela is { } t)
            {
                if (porId.TryGetValue(t.ConsultaId, out var r) && r.Ok)
                {
                    if (r.Linhas.Count == 0) doc.P("Não houve registros para o critério no período analisado.", italico: true);
                    else foreach (var tab in TabelaAnos(t, r) ?? [Tabela(t, r)]) { doc.Tab(tab); Registrar(tab); }
                }
                else doc.P("Dados indisponíveis para esta tabela.", italico: true);
            }
            // Tabela transcrita de um arquivo anexado
            ResultadoConsulta? doAnexo = null;
            TabelaPlanejada? planAnexo = null;
            if (s.TabelaAnexo is { Cabecalho.Count: > 0, Linhas.Count: > 0 } tx)
            {
                // Os valores foram digitados pela IA: cada um precisa estar no arquivo ou nos resultados do banco
                // (percentuais ficam de fora: podem ser calculados; a conferência é dos valores)
                foreach (var cel in tx.Linhas.SelectMany(l => l).Where(c => c is not null && !c.TrimEnd().EndsWith('%')))
                    foreach (System.Text.RegularExpressions.Match m in RegexNumeroBr().Matches(cel))
                        if (!numerosDisponiveis.Contains(m.Value)) valoresTabelaAnexo.Add(m.Value);
                // Mesmo padrão das demais tabelas (coluna % no fim, linha TOTAL, valores centralizados)
                (doAnexo, planAnexo) = (ResultadoDoAnexo(tx), PlanejadaDoAnexo(tx));
                var tabs = doAnexo is null ? [TabelaDoAnexo(tx)] : TabelaAnos(planAnexo, doAnexo) ?? [Tabela(planAnexo, doAnexo)];
                var fonte = "Fonte: arquivo anexado" + (string.IsNullOrWhiteSpace(tx.Fonte) ? "." : $" — {tx.Fonte.Trim()}.");
                tabs[^1] = tabs[^1] with { Nota = tabs[^1].Nota is { } n && !n.StartsWith("Fonte") ? $"{fonte} {n}" : fonte };
                foreach (var tab in tabs) { doc.Tab(tab); Registrar(tab); }
            }
            doc.Textos(s.TextoDepois);

            // Gráfico SEMPRE ao fim de cada seção com dados: o pedido pela IA ou, se ela não pediu
            // (ou pediu algo que não confere com o resultado), um escolhido pelo sistema a partir da tabela.
            GraficoDados? dados = null;
            string? tituloGrafico = null;
            if (s.Grafico is { } gp && porId.TryGetValue(gp.ConsultaId, out var rg) && rg.Ok)
            {
                dados = MontarGrafico(gp, rg);
                tituloGrafico = gp.Titulo;
            }
            if (dados is null && s.Tabela is { } ta && porId.TryGetValue(ta.ConsultaId, out var rt) && rt.Ok)
            {
                dados = GraficoAutomatico(rt, ta);
                tituloGrafico = ta.Titulo;
            }
            if (dados is null && doAnexo is { Linhas.Count: >= 2 })
            {
                dados = GraficoAutomatico(doAnexo, planAnexo);
                tituloGrafico = s.TabelaAnexo!.Titulo;
            }
            if (dados is not null)
                doc.Grafico((string.IsNullOrWhiteSpace(tituloGrafico) ? s.Titulo : tituloGrafico).ToUpper(Fmt.PtBr), dados);
        }

        doc.T1("Conclusão").Textos(e.Conclusao);
        if (!string.IsNullOrWhiteSpace(notaConclusao)) doc.P(notaConclusao, tamanho: 10);   // nota de método, discreta
        doc.P(MontadorGerencial.ParagrafoQuantitativo);
        MontadorGerencial.Encerramento(doc, opt.Value, municipio, plano.Assinatura);

        // Confere se os valores em R$ citados nos textos existem nos resultados do banco
        var textos = string.Join(" ", new[] { e.Introducao, e.Conclusao }
            .Concat(e.Secoes.SelectMany(s => new[] { s.TextoAntes, s.TextoDepois })));
        var naoConferidos = RegexMoeda().Matches(textos).Select(m => m.Groups[1].Value)
            .Where(v => !numerosDisponiveis.Contains(v)).Concat(valoresTabelaAnexo).Distinct().ToList();
        // PDF/imagem anexado: o sistema não lê os números deles, então não dá para conferir (evita falso alerta)
        if (anexos?.Any(a => a.Binario) == true) naoConferidos.Clear();
        return (doc, naoConferidos);
    }

    [GeneratedRegex(@"\d{1,3}(?:\.\d{3})*,\d{2}")] private static partial Regex RegexNumeroBr();

    /// <summary>
    /// Colunas da tabela do anexo no formato do montador: 1ª = texto; com "R$" = moeda; em % = percentual;
    /// números = numero; o resto = texto. Assim ela recebe a coluna % no fim e a linha TOTAL como as do banco.
    /// </summary>
    public static TabelaPlanejada PlanejadaDoAnexo(TabelaAnexo t)
    {
        var r = ResultadoDoAnexo(t, 1);
        return new TabelaPlanejada
        {
            ConsultaId = "anexo", Titulo = t.Titulo, LinhaTotal = true,
            Colunas = (r?.Colunas ?? []).Select((c, i) => new ColunaPlanejada
            {
                Campo = c,
                Rotulo = i < t.Cabecalho.Count ? t.Cabecalho[i] : c,
                Formato = i == 0 ? "texto"
                    : c.EndsWith("_perc") ? "percentual"
                    : r!.Linhas.All(l => l[i] is null or double) && r.Linhas.Any(l => l[i] is double)
                        ? (t.Linhas.Any(l => i < l.Count && (l[i] ?? "").Contains("R$")) ? "moeda" : "numero")
                    : "texto"
            }).ToList()
        };
    }

    /// <summary>Tabela de um anexo sem números reconhecíveis: valores como a IA transcreveu, com a fonte na nota.</summary>
    public static TabelaDoc TabelaDoAnexo(TabelaAnexo t)
    {
        var n = t.Cabecalho.Count;
        var linhas = t.Linhas.Take(MaxLinhasTabela)
            .Select(l => Enumerable.Range(0, n).Select(i => i < l.Count ? (l[i] ?? "").Trim() : "").ToArray()).ToList();
        var cab = t.Cabecalho.Select(c => c.Trim().ToUpper(Fmt.PtBr)).ToArray();
        var fonte = linhas.Count > 0 && linhas.Max(l => l.Max(c => c.Length)) > 18 ? 7.5 : 8.5;
        return new TabelaDoc
        {
            Titulo = string.IsNullOrWhiteSpace(t.Titulo) ? null : t.Titulo.ToUpper(Fmt.PtBr),
            Cabecalho = cab,
            Linhas = linhas,
            Larguras = MontadorGerencial.LargurasAuto(linhas.Append(cab).ToList(), fonte, cabecalho: cab),
            NegritoUltimas = linhas.Count > 1 && linhas[^1][0].StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
            Fonte = fonte,
            Nota = "Fonte: arquivo anexado" + (string.IsNullOrWhiteSpace(t.Fonte) ? "." : $" — {t.Fonte.Trim()}.") +
                   (t.Linhas.Count > MaxLinhasTabela ? $" Exibindo {MaxLinhasTabela} de {t.Linhas.Count} linhas." : "")
        };
    }

    /// <summary>
    /// A tabela do anexo como "resultado de consulta" (números convertidos de "R$ 1.234,56", "12,5%"), para o gráfico
    /// automático. A linha TOTAL fica de fora do gráfico; colunas em % ganham "_perc" no nome (não viram série em R$).
    /// </summary>
    public static ResultadoConsulta? ResultadoDoAnexo(TabelaAnexo t, int minLinhas = 1)
    {
        static decimal? Numero(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var x = s.Replace("R$", "").Replace("%", "").Replace(" ", "").Trim();
            var neg = x.StartsWith('(') && x.EndsWith(')') || x.StartsWith('-');
            x = x.Trim('(', ')', '-');
            if (!RegexNumeroSimples().IsMatch(x)) return null;
            var v = decimal.Parse(x.Replace(".", "").Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            return neg ? -v : v;
        }
        var linhas = t.Linhas.Where(l => l.Count > 0 && !(l[0] ?? "").Trim().StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase)).ToList();
        if (linhas.Count < minLinhas) return null;
        var colunas = t.Cabecalho.Select((c, i) =>
        {
            var nome = new string(c.Normalize(System.Text.NormalizationForm.FormD)
                .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark)
                .ToArray()).Trim().ToLowerInvariant().Replace(' ', '_');
            var pct = c.Contains('%') || linhas.All(l => i >= l.Count || string.IsNullOrWhiteSpace(l[i]) || l[i].TrimEnd().EndsWith('%'));
            nome = nome.Replace("%", "").Trim('_');
            if (string.IsNullOrEmpty(nome)) nome = $"coluna{i + 1}";
            return pct && i > 0 ? nome + "_perc" : nome;
        }).ToList();
        return new ResultadoConsulta
        {
            Id = "anexo", Objetivo = t.Titulo, Sql = "", Colunas = colunas,
            Linhas = linhas.Select(l => colunas.Select((_, i) =>
            {
                var cel = i < l.Count ? l[i] : null;
                return i > 0 && Numero(cel) is decimal d ? (object?)(double)d : cel;
            }).ToArray()).ToList()
        };
    }

    [GeneratedRegex(@"^\d{1,3}(\.\d{3})*(,\d+)?$|^\d+(,\d+)?$")] private static partial Regex RegexNumeroSimples();

    static TabelaDoc Tabela(TabelaPlanejada t, ResultadoConsulta r)
    {
        int Indice(string? campo) => campo == null ? -1 : r.Colunas.FindIndex(c => string.Equals(c, campo, StringComparison.OrdinalIgnoreCase));

        // Colunas "virtuais" (não existem no SQL, o sistema calcula): participação (basta o campoBase existir) e
        // variação (campo comparado + campoBase; sem campoBase = variação sobre a linha anterior)
        var colunas = t.Colunas.Where(c => c.Formato switch
        {
            "participacao" => Indice(c.Campo) >= 0 || Indice(c.CampoBase) >= 0,
            "variacao" => Indice(c.Campo) >= 0 && (c.CampoBase == null || Indice(c.CampoBase) >= 0),
            _ => Indice(c.Campo) >= 0
        }).ToList();
        if (colunas.Count == 0) // IA referenciou colunas inexistentes → mostra todas, com formato automático
            colunas = r.Colunas.Select(c => new ColunaPlanejada
            {
                Campo = c, Rotulo = c.Replace('_', ' ').ToUpper(Fmt.PtBr),
                Formato = r.Linhas.All(l => l[Indice(c)] is null || ExecutorSql.Numerico(l[Indice(c)])) ? "moeda" : "texto"
            }).ToList();
        colunas = ColunaPercentualNoFim(colunas, r.Linhas.Count);

        var linhasDados = r.Linhas.Take(MaxLinhasTabela).ToList();
        decimal Soma(string? campo) { var i = Indice(campo); return i < 0 ? 0 : r.Linhas.Sum(l => ExecutorSql.ParaDecimal(l[i])); }
        decimal SomaExibida(string? campo) { var i = Indice(campo); return i < 0 ? 0 : linhasDados.Sum(l => ExecutorSql.ParaDecimal(l[i])); }

        // Variação % = (comparado − base) / base. Ex.: 2025 = 100 e 2026 = 120 → 20,00%
        static string Variacao(decimal baseV, decimal atual) => Fmt.Pct(baseV == 0 ? null : (atual - baseV) / Math.Abs(baseV) * 100m);

        string Celula(ColunaPlanejada c, object? v) => c.Formato switch
        {
            _ when v is null => "",
            // ano, mês e códigos são identificadores: sem separador de milhar ("2025", não "2.025"); mês por extenso
            _ when Identificador(c.Campo) && string.Equals(c.Campo, "mes", StringComparison.OrdinalIgnoreCase)
                   && ExecutorSql.Numerico(v) && ExecutorSql.ParaDecimal(v) is >= 1 and <= 12 => Fmt.Mes((int)ExecutorSql.ParaDecimal(v)),
            _ when Identificador(c.Campo) => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            "moeda" => Fmt.Moeda(ExecutorSql.ParaDecimal(v)),
            "numero" => ExecutorSql.ParaDecimal(v) % 1 == 0 ? ExecutorSql.ParaDecimal(v).ToString("N0", Fmt.PtBr) : ExecutorSql.ParaDecimal(v).ToString("N2", Fmt.PtBr),
            "percentual" => Fmt.Pct(ExecutorSql.ParaDecimal(v)),
            "participacao" => Participacao(c, v),
            _ => Convert.ToString(v, Fmt.PtBr) ?? ""
        };

        // % da linha sobre o total da coluna base (v já é o valor do campo base nesta linha)
        string Participacao(ColunaPlanejada c, object? v)
        {
            var total = Soma(c.CampoBase ?? c.Campo);
            return Fmt.Pct(total == 0 ? null : ExecutorSql.ParaDecimal(v) / total * 100m);
        }

        var linhas = linhasDados.Select((l, n) => colunas.Select(c =>
        {
            if (c.Formato == "variacao")
            {
                var atual = ExecutorSql.ParaDecimal(l[Indice(c.Campo)]);
                if (c.CampoBase != null) return Variacao(ExecutorSql.ParaDecimal(l[Indice(c.CampoBase)]), atual);
                return n == 0 ? "-" : Variacao(ExecutorSql.ParaDecimal(linhasDados[n - 1][Indice(c.Campo)]), atual);   // vs. linha anterior
            }
            // participação: usa o valor do campoBase na mesma linha
            var campo = c.Formato == "participacao" && Indice(c.CampoBase) >= 0 ? c.CampoBase! : c.Campo;
            return Celula(c, l[Indice(campo)]);
        }).ToArray()).ToList();

        // Linha TOTAL sempre (padrão da consultoria): soma de cada coluna de valor e o total da participação.
        // Variação (comparativo entre datas/colunas) NÃO é somada: no total só os valores.
        if (linhasDados.Count >= 2)
        {
            linhas.Add(colunas.Select((c, i) => c.Formato switch
            {
                _ when i == 0 => "TOTAL",
                _ when Identificador(c.Campo) => "",   // nunca somar ano/mês/códigos
                "moeda" => Fmt.Moeda(SomaExibida(c.Campo)),
                "numero" => SomaExibida(c.Campo).ToString(SomaExibida(c.Campo) % 1 == 0 ? "N0" : "N2", Fmt.PtBr),
                "participacao" => Fmt.Pct(Soma(c.CampoBase ?? c.Campo) is var tot && tot != 0
                    ? SomaExibida(Indice(c.CampoBase) >= 0 ? c.CampoBase : c.Campo) / tot * 100m : null),
                "percentual" when !RegexColunaVariacao().IsMatch(c.Campo + " " + c.Rotulo) => Fmt.Pct(SomaExibida(c.Campo)),
                _ => ""
            }).ToArray());
        }

        var pesos = colunas.Select(c => c.Formato == "texto" ? 3.0 : 1.6).ToArray();
        var larguras = pesos.Select(p => (int)Math.Round(p / pesos.Sum() * 100)).ToArray();
        larguras[0] += 100 - larguras.Sum();

        return new TabelaDoc
        {
            Titulo = string.IsNullOrWhiteSpace(t.Titulo) ? null : t.Titulo.ToUpper(Fmt.PtBr),
            Cabecalho = colunas.Select(c => string.IsNullOrWhiteSpace(c.Rotulo) ? c.Campo.ToUpper(Fmt.PtBr) : c.Rotulo.ToUpper(Fmt.PtBr)).ToArray(),
            Linhas = linhas,
            Larguras = larguras,
            NegritoUltimas = linhasDados.Count >= 2 ? 1 : 0,
            CentralizarPrimeira = colunas[0].Formato != "texto",
            Fonte = colunas.Count > 5 ? 7.5 : 8.5,
            Nota = r.Linhas.Count > MaxLinhasTabela ? $"Exibindo {MaxLinhasTabela} de {r.Linhas.Count} linhas." : null
        };
    }

    /// <summary>
    /// Comparativo entre anos no padrão da consultoria: anos no cabeçalho, cada um seguido do seu % (variação sobre o
    /// ano anterior), as informações nas linhas e, no fim, "TOTAL %" (variação do primeiro ao último ano).
    /// A linha TOTAL soma só os valores. Aceita o resultado "longo" (uma linha por ano: ano, empenhado, pago — ou
    /// ano, função, valor) e o "largo" (uma coluna por ano: função, pago_2024, pago_2025). Devolve null se não for
    /// comparativo entre anos.
    /// </summary>
    public static List<TabelaDoc>? TabelaAnos(TabelaPlanejada t, ResultadoConsulta r)
    {
        ColunaPlanejada? Plan(int i) => t.Colunas.FirstOrDefault(c => string.Equals(c.Campo, r.Colunas[i], StringComparison.OrdinalIgnoreCase)
                                                                    && c.Formato is not ("participacao" or "variacao"));
        string Rot(int i) => Plan(i)?.Rotulo is { Length: > 0 } x ? x.ToUpper(Fmt.PtBr) : r.Colunas[i].Replace('_', ' ').ToUpper(Fmt.PtBr);
        bool Numerica(int i) => r.Linhas.All(l => l[i] is null || ExecutorSql.Numerico(l[i])) && r.Linhas.Any(l => l[i] is not null);
        bool Pct(int i) => Plan(i)?.Formato is "percentual" || RegexColunaPercentual().IsMatch(r.Colunas[i]);
        string Texto(object? v, int i) => v is null ? "" : string.Equals(r.Colunas[i], "mes", StringComparison.OrdinalIgnoreCase)
            && ExecutorSql.Numerico(v) && ExecutorSql.ParaDecimal(v) is >= 1 and <= 12 ? Fmt.Mes((int)ExecutorSql.ParaDecimal(v))
            : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";

        var todas = Enumerable.Range(0, r.Colunas.Count).ToList();
        string cabItem; List<string> itens; List<int> anos; Dictionary<(string, int), decimal> val = []; string formato = "moeda";

        var iAno = todas.FirstOrDefault(i => r.Colunas[i].ToLowerInvariant() is "ano" or "exercicio" or "ano_exercicio", -1);
        if (iAno >= 0 && r.Linhas.Select(l => l[iAno]).Distinct().Count() >= 2)
        {
            // Longo: uma linha por ano
            var metricas = todas.Where(i => i != iAno && !Identificador(r.Colunas[i]) && Numerica(i) && !Pct(i)).ToList();
            var iItem = todas.FirstOrDefault(i => i != iAno && !metricas.Contains(i) && !Pct(i)
                                                  && r.Linhas.Select(l => l[i]).Distinct().Count() > 1, -1);
            if (metricas.Count == 0) return null;
            // ordem das métricas = ordem da tabela planejada
            metricas = metricas.OrderBy(i => t.Colunas.FindIndex(c => string.Equals(c.Campo, r.Colunas[i], StringComparison.OrdinalIgnoreCase)) is var k and >= 0 ? k : 99).ToList();
            formato = Plan(metricas[0])?.Formato == "numero" ? "numero" : "moeda";
            anos = r.Linhas.Select(l => (int)ExecutorSql.ParaDecimal(l[iAno])).Distinct().Order().ToList();
            if (iItem >= 0)
            {   // ano × item (função, mês...): linhas = itens, uma métrica
                cabItem = Rot(iItem);
                itens = r.Linhas.Select(l => Texto(l[iItem], iItem)).Distinct().ToList();
                foreach (var l in r.Linhas)
                {
                    var k = (Texto(l[iItem], iItem), (int)ExecutorSql.ParaDecimal(l[iAno]));
                    val[k] = val.GetValueOrDefault(k) + ExecutorSql.ParaDecimal(l[metricas[0]]);
                }
            }
            else
            {   // linhas = as informações (empenhado, pago...)
                cabItem = "DESCRIÇÃO";
                itens = metricas.Select(i => Fmt.PtBr.TextInfo.ToTitleCase(Rot(i).ToLower(Fmt.PtBr))).ToList();
                foreach (var l in r.Linhas)
                    for (int m = 0; m < metricas.Count; m++)
                    {
                        var k = (itens[m], (int)ExecutorSql.ParaDecimal(l[iAno]));
                        val[k] = val.GetValueOrDefault(k) + ExecutorSql.ParaDecimal(l[metricas[m]]);
                    }
            }
        }
        else
        {
            // Largo: colunas cujo nome/rótulo traz o ano (pago_2024, "2025"...)
            var porAno = todas.Where(i => Numerica(i) && !Pct(i) && !Identificador(r.Colunas[i]))
                .Select(i => (i, m: RegexAno().Match(r.Colunas[i] + " " + (Plan(i)?.Rotulo ?? ""))))
                .Where(x => x.m.Success).GroupBy(x => int.Parse(x.m.Value)).ToDictionary(g => g.Key, g => g.First().i);
            if (porAno.Count < 2) return null;
            var iItem = todas.FirstOrDefault(i => !porAno.ContainsValue(i) && !Numerica(i), -1);
            if (iItem < 0) iItem = todas.FirstOrDefault(i => !porAno.ContainsValue(i), -1);
            if (iItem < 0) return null;
            anos = porAno.Keys.Order().ToList();
            formato = Plan(porAno[anos[0]])?.Formato == "numero" ? "numero" : "moeda";
            cabItem = Rot(iItem);
            itens = r.Linhas.Select(l => Texto(l[iItem], iItem)).Distinct().ToList();
            foreach (var l in r.Linhas)
                foreach (var (ano, i) in porAno)
                {
                    var k = (Texto(l[iItem], iItem), ano);
                    val[k] = val.GetValueOrDefault(k) + ExecutorSql.ParaDecimal(l[i]);
                }
        }
        if (anos.Count < 2 || anos.Count > 20 || itens.Count == 0) return null;
        itens = itens.Take(MaxLinhasTabela).ToList();

        string V(decimal v) => formato == "numero" ? v.ToString(v % 1 == 0 ? "N0" : "N2", Fmt.PtBr) : Fmt.Moeda(v);
        static string Var(decimal b, decimal a) => Fmt.Pct(b == 0 ? null : (a - b) / Math.Abs(b) * 100m);

        // % só na evolução de cada período (2024→2025, 2025→2026): o primeiro ano não tem %.
        // "TOTAL %" (primeiro → último ano) só com 3+ anos — com 2 anos seria igual ao único %.
        var comTotalPct = anos.Count >= 3;

        // Muitos anos não cabem na largura da página: a tabela continua HORIZONTAL, quebrada em blocos de até 4 anos
        // (ex.: 2020–2023 e 2024–2026), cada bloco repetindo a 1ª coluna; o % do 1º ano de um bloco compara com o
        // último ano do bloco anterior, e o "TOTAL %" fica no último bloco.
        var nBlocos = (anos.Count + 3) / 4;
        var blocos = new List<List<int>>();
        for (int b = 0, k = 0; b < nBlocos; b++)
        {
            var tam = anos.Count / nBlocos + (b < anos.Count % nBlocos ? 1 : 0);
            blocos.Add(Enumerable.Range(k, tam).ToList());
            k += tam;
        }

        var tabelas = new List<TabelaDoc>();
        foreach (var bloco in blocos)
        {
            var ultimo = bloco == blocos[^1];
            var cab = new List<string> { cabItem };
            foreach (var k in bloco) { cab.Add(anos[k].ToString()); if (k > 0) cab.Add("%"); }
            if (ultimo && comTotalPct) cab.Add("TOTAL %");

            var linhas = itens.Select(it =>
            {
                var c = new List<string> { it };
                foreach (var k in bloco)
                {
                    var v = val.GetValueOrDefault((it, anos[k]));
                    c.Add(V(v));
                    if (k > 0) c.Add(Var(val.GetValueOrDefault((it, anos[k - 1])), v));
                }
                if (ultimo && comTotalPct) c.Add(Var(val.GetValueOrDefault((it, anos[0])), val.GetValueOrDefault((it, anos[^1]))));
                return c.ToArray();
            }).ToList();
            if (itens.Count >= 2)
            {
                var tot = new List<string> { "TOTAL" };
                foreach (var k in bloco) { tot.Add(V(itens.Sum(it => val.GetValueOrDefault((it, anos[k]))))); if (k > 0) tot.Add(""); }
                if (ultimo && comTotalPct) tot.Add("");
                linhas.Add(tot.ToArray());
            }

            var nAnos = bloco.Count;
            var fonte = nAnos >= 4 ? 6.5 : nAnos == 3 ? 7.5 : 8.5;
            tabelas.Add(new TabelaDoc
            {
                Titulo = tabelas.Count > 0 || string.IsNullOrWhiteSpace(t.Titulo) ? null : t.Titulo.ToUpper(Fmt.PtBr),
                Cabecalho = cab.ToArray(),
                Linhas = linhas,
                Larguras = MontadorGerencial.LargurasAuto(linhas, fonte, cabecalho: cab.ToArray(), minPorTexto: nAnos >= 4 ? 14 : 20),
                NegritoUltimas = itens.Count >= 2 ? 1 : 0,
                Fonte = fonte
            });
        }
        // Legenda do "***" no último bloco, se aparecer em qualquer bloco
        if (tabelas.Any(tb => tb.Linhas.Any(l => l.Contains("***"))))
            tabelas[^1] = tabelas[^1] with { Nota = "Legenda: *** Sem valor no ano anterior para comparação." };
        return tabelas;
    }

    [GeneratedRegex(@"(?<!\d)(19|20)\d{2}(?!\d)")] private static partial Regex RegexAno();

    static bool EhPercentual(ColunaPlanejada c) =>
        c.Formato is "percentual" or "participacao" or "variacao" || RegexColunaPercentual().IsMatch(c.Campo);

    /// <summary>
    /// Padrão da consultoria: a ÚLTIMA coluna de toda tabela é um %. Se a IA pôs o % no meio, ele vai para o fim;
    /// se não pôs nenhum, o sistema cria: 2 colunas de valor → variação entre elas (ex.: 2025 × 2026);
    /// 1 coluna de valor por data (ano/mês) → variação sobre a linha anterior; nos demais casos → participação.
    /// </summary>
    public static List<ColunaPlanejada> ColunaPercentualNoFim(List<ColunaPlanejada> colunas, int linhas = 2)
    {
        if (colunas.Count == 0 || EhPercentual(colunas[^1])) return colunas;
        var pct = colunas.Where(EhPercentual).ToList();
        if (pct.Count > 0) return [.. colunas.Except(pct), .. pct];

        var valores = colunas.Skip(1).Where(c => c.Formato is "moeda" or "numero" && !Identificador(c.Campo)).ToList();
        if (valores.Count == 0) return colunas;
        ColunaPlanejada nova;
        if (valores.Count == 2)
            nova = new() { Campo = valores[1].Campo, CampoBase = valores[0].Campo, Formato = "variacao", Rotulo = "VARIAÇÃO %" };
        else if (valores.Count == 1 && CamposTempo.Contains(colunas[0].Campo.ToLowerInvariant()))
            nova = new() { Campo = valores[0].Campo, Formato = "variacao", Rotulo = "VARIAÇÃO %" };
        else if (linhas < 2) return colunas;   // 1 linha: participação seria sempre 100%
        else
            nova = new() { Campo = valores[^1].Campo, CampoBase = valores[^1].Campo, Formato = "participacao", Rotulo = "PART. %" };
        return [.. colunas, nova];
    }

    [GeneratedRegex(@"varia|cresc|dif|evolu|\bvar\b", RegexOptions.IgnoreCase)] private static partial Regex RegexColunaVariacao();

    /// <summary>Colunas que identificam (ano, mês, códigos, números de empenho) — não são grandezas somáveis.</summary>
    public static bool Identificador(string campo)
    {
        var c = campo.ToLowerInvariant();
        return c is "ano" or "mes" or "exercicio" or "ano_exercicio" || c.StartsWith("cd_") || c.StartsWith("codigo")
               || c.StartsWith("nr_") || c.EndsWith("_id") || c == "id";
    }

    /// <summary>Valida o gráfico pedido pela IA contra o resultado real; devolve null se não fizer sentido.</summary>
    public static GraficoDados? MontarGrafico(GraficoPlanejado gp, ResultadoConsulta r)
    {
        int Indice(string campo) => r.Colunas.FindIndex(c => string.Equals(c, campo, StringComparison.OrdinalIgnoreCase));
        var iCat = Indice(gp.CampoCategoria);
        var iVals = gp.CamposValor.Select(Indice).Where(i => i >= 0 && i != iCat && !Identificador(r.Colunas[i]))
            .Where(i => r.Linhas.All(l => l[i] is null || ExecutorSql.Numerico(l[i]))).Distinct().Take(GeradorGraficos.MaxSeries).ToList();
        if (iCat < 0 || iVals.Count == 0 || r.Linhas.Count < 2) return null;

        // Pizza: parte do todo de UMA série com valores positivos (excedente agrupado em "DEMAIS")
        if (gp.Tipo == "pizza")
        {
            var itens = r.Linhas.Select(l => (rotulo: Convert.ToString(l[iCat], Fmt.PtBr) ?? "", valor: ExecutorSql.ParaDecimal(l[iVals[0]]))).ToList();
            if (itens.Any(x => x.valor < 0) || itens.Count(x => x.valor > 0) < 2) return null;
            return Graficos.Rosca(itens, itens.Sum(x => x.valor));
        }

        var tipo = gp.Tipo switch { "barras" => TipoGrafico.Barras, "linha" => TipoGrafico.Linha, _ => TipoGrafico.Colunas };
        var linhas = r.Linhas.ToList();
        // Itens com nome (funções, credores, órgãos) em colunas ficam com o rótulo cortado: viram barras horizontais
        var categoriaTempo = CamposTempo.Contains(r.Colunas[iCat].ToLowerInvariant());
        if (tipo == TipoGrafico.Colunas && !categoriaTempo && linhas.Count > 6) tipo = TipoGrafico.Barras;
        if (tipo == TipoGrafico.Barras)
            linhas = linhas.OrderByDescending(l => ExecutorSql.ParaDecimal(l[iVals[0]])).Take(iVals.Count > 1 ? 10 : 15).ToList();
        if (tipo == TipoGrafico.Colunas && linhas.Count > 24) return null;
        if (tipo == TipoGrafico.Linha && linhas.Count > 48) return null;

        var nomes = gp.NomesSeries;
        // Colunas de % nunca são exibidas como R$ (mesmo que a IA tenha deixado o formato "moeda")
        var formato = gp.Formato is "numero" or "percentual" ? gp.Formato
            : iVals.All(i => RegexColunaPercentual().IsMatch(r.Colunas[i])) ? "percentual" : "moeda";
        return new GraficoDados(tipo,
            linhas.Select(l => Convert.ToString(l[iCat], Fmt.PtBr) ?? "").ToArray(),
            iVals.Select((iv, k) => new SerieGrafico(
                k < nomes.Count && !string.IsNullOrWhiteSpace(nomes[k]) ? nomes[k] : r.Colunas[iv].Replace('_', ' '),
                // Célula vazia = sem valor naquele ponto (ex.: realizado × projetado) — não é zero
                linhas.Select(l => l[iv] is null ? double.NaN : (double)ExecutorSql.ParaDecimal(l[iv])).ToArray())).ToList(),
            formato);
    }

    static readonly string[] CamposTempo = ["mes_nome", "mes", "periodo", "ano", "exercicio", "ano_exercicio", "data", "competencia"];

    /// <summary>
    /// Escolhe um gráfico para o resultado de uma tabela quando a IA não pediu um:
    /// tempo → colunas/linha; ano × mês ou município × item → séries por grupo; parte do todo → pizza (até 5 itens)
    /// ou ranking em barras; uma linha com vários valores → colunas por coluna. Devolve null se não houver o que grafar.
    /// </summary>
    public static GraficoDados? GraficoAutomatico(ResultadoConsulta r, TabelaPlanejada? t = null)
    {
        if (r.Linhas.Count == 0) return null;
        int Indice(string campo) => r.Colunas.FindIndex(c => string.Equals(c, campo, StringComparison.OrdinalIgnoreCase));
        bool Numerica(int i) => r.Linhas.All(l => l[i] is null || ExecutorSql.Numerico(l[i])) && r.Linhas.Any(l => l[i] is not null);
        // colunas % calculadas (participação/variação) apontam para a coluna de valor: não definem o formato dela
        ColunaPlanejada? Planejada(int i) => t?.Colunas.FirstOrDefault(c => string.Equals(c.Campo, r.Colunas[i], StringComparison.OrdinalIgnoreCase)
                                                                          && c.Formato is not ("participacao" or "variacao"));
        string Rotulo(int i) => Planejada(i)?.Rotulo
                                is { Length: > 0 } rot ? Fmt.PtBr.TextInfo.ToTitleCase(rot.ToLower(Fmt.PtBr)) : r.Colunas[i].Replace('_', ' ');
        string? Formato(int i) => Planejada(i)?.Formato;
        bool Percentual(int i) => Formato(i) is "percentual" or "participacao" || RegexColunaPercentual().IsMatch(r.Colunas[i]);

        var todas = Enumerable.Range(0, r.Colunas.Count).ToList();
        var valores = todas.Where(i => !Identificador(r.Colunas[i]) && Numerica(i) && !Percentual(i)).ToList();
        // valores na ordem da tabela planejada (as colunas que o leitor vê primeiro)
        if (t is not null)
            valores = valores.OrderBy(i => t.Colunas.FindIndex(c => string.Equals(c.Campo, r.Colunas[i], StringComparison.OrdinalIgnoreCase)) is var k and >= 0 ? k : 99).ToList();
        var formato = valores.Count > 0 && Formato(valores[0]) == "numero" ? "numero" : "moeda";
        if (valores.Count == 0) return null;

        // Linha "TOTAL" vinda do SQL não entra no gráfico
        var cats = todas.Where(i => !valores.Contains(i) && !Percentual(i)).ToList();
        var linhas = r.Linhas.Where(l => !cats.Any(i => string.Equals(Convert.ToString(l[i])?.Trim(), "TOTAL", StringComparison.OrdinalIgnoreCase))).ToList();

        // 1 linha com 2+ valores (ex.: empenhado, liquidado, pago): uma coluna para cada valor
        if (linhas.Count == 1)
        {
            if (valores.Count < 2) return null;
            var vs = valores.Take(6).ToList();
            return new GraficoDados(TipoGrafico.Colunas, vs.Select(Rotulo).ToArray(),
                [new SerieGrafico("Valor", vs.Select(i => (double)ExecutorSql.ParaDecimal(linhas[0][i])).ToArray())], formato);
        }

        // Categoria: mês por extenso > texto > ano/mês numéricos; "mes" numérico some quando há "mes_nome"
        var iMesNome = Indice("mes_nome");
        if (iMesNome >= 0) cats.Remove(Indice("mes"));
        if (cats.Count == 0) return null;
        string Texto(object? v, int col) => string.Equals(r.Colunas[col], "mes", StringComparison.OrdinalIgnoreCase) && ExecutorSql.Numerico(v)
                                            && ExecutorSql.ParaDecimal(v) is >= 1 and <= 12
            ? Fmt.MesCurto((int)ExecutorSql.ParaDecimal(v)) : Convert.ToString(v, Fmt.PtBr) ?? "";
        bool Tempo(int i) => CamposTempo.Contains(r.Colunas[i].ToLowerInvariant());
        // colunas com um único valor (ex.: o mesmo município em todas as linhas) não servem de categoria
        var variam = cats.Where(i => linhas.Select(l => Texto(l[i], i)).Distinct().Count() > 1).ToList();
        if (variam.Count > 0) cats = variam;

        // Duas categorias + 1 valor (ex.: ano × mês, município × função): a de poucos valores vira série
        if (cats.Count >= 2)
        {
            var porDistintos = cats.Select(i => (i, n: linhas.Select(l => Texto(l[i], i)).Distinct().Count())).OrderBy(x => x.n).ToList();
            var serie = porDistintos[0];
            var categoria = porDistintos.FirstOrDefault(x => x.i != serie.i && Tempo(x.i) && x.n > 1);
            if (categoria.n == 0) categoria = porDistintos.Last();
            if (serie.n is >= 2 and <= GeradorGraficos.MaxSeries && categoria.n is >= 2 and <= 24)
            {
                var v = valores[0];
                var nomesSerie = linhas.Select(l => Texto(l[serie.i], serie.i)).Distinct().ToList();
                var nomesCat = linhas.Select(l => Texto(l[categoria.i], categoria.i)).Distinct().ToList();
                if (!Tempo(categoria.i))   // itens: os de maior valor primeiro, até 10
                    nomesCat = nomesCat.OrderByDescending(n => linhas.Where(l => Texto(l[categoria.i], categoria.i) == n).Sum(l => ExecutorSql.ParaDecimal(l[v]))).Take(10).ToList();
                var series = nomesSerie.Select(ns => new SerieGrafico(ns, nomesCat.Select(nc =>
                {
                    var l = linhas.FirstOrDefault(x => Texto(x[serie.i], serie.i) == ns && Texto(x[categoria.i], categoria.i) == nc);
                    return l is null || l[v] is null ? double.NaN : (double)ExecutorSql.ParaDecimal(l[v]);
                }).ToArray())).ToList();
                var tipo2 = Tempo(categoria.i) ? (nomesCat.Count > 12 ? TipoGrafico.Linha : TipoGrafico.Colunas) : TipoGrafico.Barras;
                return new GraficoDados(tipo2, nomesCat.ToArray(), series, formato);
            }
        }

        // Uma categoria
        var iCat = cats.Any(i => !Tempo(i))
            ? cats.Where(i => !Tempo(i)).OrderByDescending(i => linhas.Select(l => Texto(l[i], i)).Distinct().Count()).First()
            : iMesNome >= 0 && cats.Contains(iMesNome) ? iMesNome : cats[0];
        var rotulos = linhas.Select(l => Texto(l[iCat], iCat)).ToArray();

        if (Tempo(iCat))
        {
            if (linhas.Count > 48) return null;
            var vs = valores.Take(GeradorGraficos.MaxSeries).ToList();
            return new GraficoDados(linhas.Count > 12 ? TipoGrafico.Linha : TipoGrafico.Colunas, rotulos,
                vs.Select(i => new SerieGrafico(Rotulo(i), linhas.Select(l => l[i] is null ? double.NaN : (double)ExecutorSql.ParaDecimal(l[i])).ToArray())).ToList(),
                formato);
        }

        // Itens (credores, funções, municípios...) com 2+ valores: barras agrupadas dos 10 maiores
        if (valores.Count >= 2)
        {
            var vs = valores.Take(GeradorGraficos.MaxSeries).ToList();
            var top = linhas.OrderByDescending(l => ExecutorSql.ParaDecimal(l[vs[0]])).Take(10).ToList();
            return new GraficoDados(TipoGrafico.Barras, top.Select(l => Texto(l[iCat], iCat)).ToArray(),
                vs.Select(i => new SerieGrafico(Rotulo(i), top.Select(l => (double)ExecutorSql.ParaDecimal(l[i])).ToArray())).ToList(), formato);
        }

        // Parte do todo de uma série: pizza com até 5 itens, ranking em barras acima disso
        var itens = linhas.Select(l => (rotulo: Texto(l[iCat], iCat), valor: ExecutorSql.ParaDecimal(l[valores[0]]))).ToList();
        if (itens.Count(x => x.valor > 0) < 2) return null;
        var total = itens.Sum(x => x.valor);
        if (itens.Any(x => x.valor < 0) || formato != "moeda")
        {
            var top = itens.OrderByDescending(x => x.valor).Take(15).ToList();
            return new GraficoDados(TipoGrafico.Barras, top.Select(x => x.rotulo).ToArray(),
                [new SerieGrafico(Rotulo(valores[0]), top.Select(x => (double)x.valor).ToArray())], formato);
        }
        return itens.Count <= GeradorGraficos.MaxFatias
            ? Graficos.Rosca(itens, total)
            : Graficos.Ranking(itens, total, 10);
    }

    static void RegistrarNumeros(ResultadoConsulta r, HashSet<string> nums)
    {
        // Diferenças entre valores da mesma linha (ex.: empenhado − pago) também contam como conferidas
        foreach (var l in r.Linhas)
        {
            var vals = l.Where(ExecutorSql.Numerico).Select(ExecutorSql.ParaDecimal).ToList();
            for (int a = 0; a < vals.Count; a++)
                for (int b = a + 1; b < vals.Count; b++)
                    nums.Add(Math.Abs(vals[a] - vals[b]).ToString("N2", Fmt.PtBr));
        }
        for (int i = 0; i < r.Colunas.Count; i++)
        {
            if (!r.Linhas.Any(l => ExecutorSql.Numerico(l[i]))) continue;
            // Diferenças entre linhas da mesma coluna (ex.: ISS 2026 − ISS 2025), em resultados pequenos
            if (r.Linhas.Count <= 30)
            {
                var col = r.Linhas.Where(l => ExecutorSql.Numerico(l[i])).Select(l => ExecutorSql.ParaDecimal(l[i])).ToList();
                for (int a = 0; a < col.Count; a++)
                    for (int b = a + 1; b < col.Count; b++)
                        nums.Add(Math.Abs(col[a] - col[b]).ToString("N2", Fmt.PtBr));
            }
            foreach (var l in r.Linhas) if (ExecutorSql.Numerico(l[i])) nums.Add(Math.Abs(ExecutorSql.ParaDecimal(l[i])).ToString("N2", Fmt.PtBr));
            nums.Add(Math.Abs(r.Linhas.Sum(l => ExecutorSql.ParaDecimal(l[i]))).ToString("N2", Fmt.PtBr));
        }
    }

    [GeneratedRegex("perc|pct|particip|variacao|_%", RegexOptions.IgnoreCase)] private static partial Regex RegexColunaPercentual();

    // Valores por extenso ("R$ 14,79 milhões", "R$ 800 mil") são arredondamentos: não entram na conferência exata
    [GeneratedRegex(@"R\$\s*\(?-?\s*(\d{1,3}(?:\.\d{3})*,\d{2})(?!\s*(?:mil\b|milh|bilh|mi\b|bi\b))")] private static partial Regex RegexMoeda();
}
