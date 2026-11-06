using RelatorioIA.Api.Models;

namespace RelatorioIA.Api.Services.Consultas;

public record PontoMensal(int Ano, int Mes, decimal Valor);

/// <summary>SemDados = mês que JÁ PASSOU mas ainda não foi publicado pelo TCESP: não recebe valor nenhum (nem estimado).</summary>
public record MesProjetado(int Ano, int Mes, decimal? Realizado, decimal? Tendencia, decimal? Estavel, bool SemDados = false);

public record AnoProjetado(int Ano, decimal Realizado, decimal TotalTendencia, decimal TotalEstavel, int MesesRealizados,
    int MesesSemDados = 0, decimal? MargemErro = null)
{
    /// <summary>% do total estimado (tendência) que já foi realizado.</summary>
    public decimal? PercentualRealizado => TotalTendencia == 0 ? null : Realizado / TotalTendencia * 100m;

    /// <summary>Margem de erro (95%) em % do total estimado — como em pesquisas: "X ± Y%".</summary>
    public decimal? MargemPercentual => MargemErro is null || TotalTendencia == 0 ? null : MargemErro / TotalTendencia * 100m;
}

/// <summary>
/// Estudo estatístico da amostra de variações mensais (mesmo mês, ano atual × ano anterior), em pontos percentuais.
/// A margem de erro usa a distribuição t de Student com 95% de confiança: margem = t × desvio padrão ÷ √n.
/// </summary>
public record EstatisticaProjecao(int Meses, decimal Media, decimal DesvioPadrao, decimal Mediana, decimal ErroPadrao,
    decimal T, decimal Margem, int Confianca = 95);

public record ResultadoProjecao(List<MesProjetado> Meses, List<AnoProjetado> Anos, decimal TaxaCrescimento,
    string BaseTaxa, string Metodo, EstatisticaProjecao? Estatistica = null);

/// <summary>
/// Projeção mensal SIMPLES e explicável (determinística — a IA nunca calcula projeções):
///  • Taxa de crescimento g = MEDIANA das variações mês a mês (mesmo mês do último ano × ano anterior) — resistente a
///    picos isolados (ex.: empenhos globais/estimativos concentrados no início do ano).
///  • Cenário tendência: mês futuro = mesmo mês do ano anterior (realizado ou referência) × (1 + g).
///  • Cenário estável:   mês futuro = mesmo mês do ano anterior (sem crescimento).
///  • Estudo estatístico: média, desvio padrão e margem de erro (95%) das variações mensais; a margem em R$ de cada ano
///    = margem da taxa × soma das bases dos meses projetados (aproximação que não acumula a incerteza entre anos).
/// Requer pelo menos 1 ano completo de histórico. Não substitui o planejamento orçamentário oficial (LOA/PPA).
///
/// REGRA: do mês atual para trás só existe dado REAL. Mês passado ainda não publicado fica "sem dados"
/// (nenhum valor exibido); a projeção só começa no mês atual. Internamente, para encadear a sazonalidade do ano
/// seguinte, usa-se o mesmo mês do ano anterior × (1+g) como referência — esse valor nunca é exibido.
/// </summary>
public static class Projetor
{
    public const decimal TaxaMaxima = 1.0m;   // limita a ±100% a.a. para evitar extrapolações absurdas

    // t de Student bicaudal 95% por graus de liberdade (1..30); acima de 30 ≈ 1,96
    static readonly decimal[] T95 =
    [
        0m, 12.706m, 4.303m, 3.182m, 2.776m, 2.571m, 2.447m, 2.365m, 2.306m, 2.262m, 2.228m, 2.201m, 2.179m, 2.160m,
        2.145m, 2.131m, 2.120m, 2.110m, 2.101m, 2.093m, 2.086m, 2.080m, 2.074m, 2.069m, 2.064m, 2.060m, 2.056m, 2.052m,
        2.048m, 2.045m, 2.042m
    ];

    /// <param name="hoje">Data de referência: meses anteriores ao mês de <paramref name="hoje"/> nunca são estimados.</param>
    public static ResultadoProjecao? Projetar(IReadOnlyList<PontoMensal> historico, int ateAno, int ateMes, DateOnly hoje)
    {
        var dados = historico.GroupBy(p => (p.Ano, p.Mes)).ToDictionary(g => g.Key, g => g.Sum(x => x.Valor));
        if (dados.Count == 0) return null;
        var anos = dados.Keys.Select(k => k.Ano).Distinct().OrderBy(a => a).ToList();
        var ultimoAno = anos[^1];
        var ultimoMes = dados.Keys.Where(k => k.Ano == ultimoAno).Max(k => k.Mes);
        var anoBase = ultimoAno - 1;
        // Precisa do ano anterior completo como referência de sazonalidade
        if (Enumerable.Range(1, 12).Any(m => !dados.ContainsKey((anoBase, m)))) return null;
        if (ateAno < ultimoAno || (ateAno == ultimoAno && ateMes <= ultimoMes)) return null;
        // O horizonte precisa alcançar o mês atual: períodos já passados não são projetados
        if (ateAno < hoje.Year || (ateAno == hoje.Year && ateMes < hoje.Month)) return null;
        ateAno = Math.Min(ateAno, Math.Max(ultimoAno, hoje.Year) + 3);   // horizonte máximo de 3 anos
        bool Passado(int a, int m) => a < hoje.Year || (a == hoje.Year && m < hoje.Month);

        // Amostra: variações mês a mês (mesmo mês do último ano × ano anterior)
        var razoes = Enumerable.Range(1, ultimoMes)
            .Where(m => dados.GetValueOrDefault((anoBase, m)) > 0 && dados.ContainsKey((ultimoAno, m)))
            .Select(m => dados[(ultimoAno, m)] / dados[(anoBase, m)]).OrderBy(x => x).ToList();
        var mediana = razoes.Count == 0 ? 1m
            : razoes.Count % 2 == 1 ? razoes[razoes.Count / 2] : (razoes[razoes.Count / 2 - 1] + razoes[razoes.Count / 2]) / 2;
        var g = Math.Clamp(Math.Round(mediana - 1m, 6), -TaxaMaxima, TaxaMaxima);
        var baseTaxa = $"a mediana das variações mês a mês de {Fmt.PeriodoExtenso(1, ultimoMes).ToLower(Fmt.PtBr)} de {ultimoAno} " +
                       $"em relação aos mesmos meses de {anoBase}";
        var est = Estatistica(razoes, g);

        var tendencia = new Dictionary<(int, int), decimal>();
        var estavel = new Dictionary<(int, int), decimal>();
        var baseProjetada = new Dictionary<int, decimal>();   // ano → soma das bases dos meses projetados (para a margem)
        var meses = new List<MesProjetado>();
        for (var ano = anoBase; ano <= ateAno; ano++)
            for (var mes = 1; mes <= 12; mes++)
            {
                if (ano == ateAno && mes > ateMes) break;
                if (dados.TryGetValue((ano, mes), out var real) && (ano < ultimoAno || mes <= ultimoMes))
                {
                    tendencia[(ano, mes)] = real; estavel[(ano, mes)] = real;
                    meses.Add(new MesProjetado(ano, mes, real, null, null));
                    continue;
                }
                var baseMes = tendencia[(ano - 1, mes)];
                var t = baseMes * (1 + g);
                var e = estavel[(ano - 1, mes)];
                tendencia[(ano, mes)] = t; estavel[(ano, mes)] = e;   // referência interna para o ano seguinte
                if (Passado(ano, mes))
                    meses.Add(new MesProjetado(ano, mes, null, null, null, SemDados: true));   // passado: nunca estimar
                else
                {
                    meses.Add(new MesProjetado(ano, mes, null, Math.Round(t, 2), Math.Round(e, 2)));
                    baseProjetada[ano] = baseProjetada.GetValueOrDefault(ano) + baseMes;
                }
            }

        var porAno = meses.GroupBy(m => m.Ano).Select(a => new AnoProjetado(a.Key,
            a.Sum(m => m.Realizado ?? 0),
            a.Sum(m => m.Realizado ?? m.Tendencia ?? 0),
            a.Sum(m => m.Realizado ?? m.Estavel ?? 0),
            a.Count(m => m.Realizado != null),
            a.Count(m => m.SemDados),
            MargemDoAno(a.Key))).ToList();

        // Ano só com dados realizados: margem zero. Ano com meses projetados: margem da taxa × base desses meses.
        decimal? MargemDoAno(int ano) =>
            !baseProjetada.TryGetValue(ano, out var b) ? 0m
            : est is null ? null
            : Math.Round(b * est.Margem / 100m, 2);
        var semDados = meses.Where(m => m.SemDados).Select(m => $"{Fmt.MesCurto(m.Mes).ToLower(Fmt.PtBr)}/{m.Ano}").ToList();

        // Nota de método SIMPLES (vai na conclusão, com pouca ênfase)
        var metodo =
            "As projeções foram estimadas a partir do histórico mensal publicado pelo TCESP, mantendo a sazonalidade de cada " +
            $"mês e aplicando a variação mediana observada entre os anos ({Fmt.Pct(g * 100m)})" +
            (est is null ? ". " : $", com margem de erro de ±{Fmt.Pct(est.Margem)} e {est.Confianca}% de confiança. ") +
            (semDados.Count > 0
                ? $"Os meses {string.Join(", ", semDados)}, ainda não publicados de forma completa, não foram estimados. "
                : "") +
            "Os valores devem ser revisados a cada novo mês de dados.";
        return new ResultadoProjecao(meses, porAno, g, baseTaxa, metodo, est);
    }

    /// <summary>Média, desvio padrão (amostral), erro padrão e margem de erro (t de Student, 95%) das variações, em p.p.</summary>
    static EstatisticaProjecao? Estatistica(List<decimal> razoes, decimal taxaAdotada)
    {
        var n = razoes.Count;
        if (n < 2) return null;
        var v = razoes.Select(r => (double)((r - 1m) * 100m)).ToList();
        var media = v.Average();
        var dp = Math.Sqrt(v.Sum(x => (x - media) * (x - media)) / (n - 1));
        var ep = dp / Math.Sqrt(n);
        var t = n - 1 < T95.Length ? T95[n - 1] : 1.96m;
        return new EstatisticaProjecao(n, R(media), R(dp), R((double)taxaAdotada * 100), R(ep), t, R(ep * (double)t));
        static decimal R(double x) => Math.Round((decimal)x, 2);
    }

    /// <summary>Lê o histórico de uma consulta que traga colunas ano, mes e um valor numérico.</summary>
    public static List<PontoMensal>? LerHistorico(ResultadoConsulta r, string? campoValor)
    {
        int I(string n) => r.Colunas.FindIndex(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase));
        var iAno = I("ano"); var iMes = I("mes");
        var iVal = campoValor != null ? I(campoValor) : -1;
        if (iVal < 0) iVal = Enumerable.Range(0, r.Colunas.Count)
            .FirstOrDefault(i => i != iAno && i != iMes && r.Linhas.Count > 0 && r.Linhas.All(l => ExecutorSql.Numerico(l[i]) || l[i] is null), -1);
        if (iAno < 0 || iMes < 0 || iVal < 0) return null;
        return r.Linhas.Where(l => ExecutorSql.Numerico(l[iAno]) && ExecutorSql.Numerico(l[iMes]))
            .Select(l => new PontoMensal((int)ExecutorSql.ParaDecimal(l[iAno]), (int)ExecutorSql.ParaDecimal(l[iMes]), ExecutorSql.ParaDecimal(l[iVal])))
            .Where(p => p.Mes is >= 1 and <= 12).ToList();
    }

    /// <summary>Converte a projeção em "resultados de consulta" para o Prompt 2 montar tabelas e gráficos.</summary>
    public static (ResultadoConsulta mensal, ResultadoConsulta anual, ResultadoConsulta estatistica) ComoResultados(ResultadoProjecao p, string metrica)
    {
        // No último mês realizado, as linhas de projeção partem do valor real (gráfico contínuo) — só se o mês
        // seguinte for projetado; se houver meses sem dados publicados no meio, o vão fica visível no gráfico.
        var ultimoReal = p.Meses.FindLastIndex(m => m.Realizado != null);
        if (ultimoReal + 1 < p.Meses.Count && p.Meses[ultimoReal + 1].SemDados) ultimoReal = -1;
        var mensal = new ResultadoConsulta
        {
            Id = "projecao_mensal",
            Objetivo = $"{metrica}: realizado e projetado por mês (tendência e estável)",
            Sql = "(calculado pelo sistema a partir do histórico)",
            Colunas = ["periodo", "situacao", "realizado", "projetado_tendencia", "projetado_estavel"],
            Linhas = p.Meses.Select((m, i) => new object?[]
            {
                $"{Fmt.MesCurto(m.Mes)}/{m.Ano}",
                m.Realizado != null ? "Realizado" : m.SemDados ? "Sem dados publicados" : "Projetado",
                (double?)m.Realizado,
                i == ultimoReal ? (double?)m.Realizado : (double?)m.Tendencia,
                i == ultimoReal ? (double?)m.Realizado : (double?)m.Estavel
            }).ToList()
        };
        var anual = new ResultadoConsulta
        {
            Id = "projecao_anual",
            Objetivo = $"{metrica}: totais por ano (realizado, estimativas, % já realizado e margem de erro de 95%)",
            Sql = "(calculado pelo sistema a partir do histórico)",
            Colunas = ["ano", "meses_realizados", "meses_sem_dados", "realizado", "total_estimado_tendencia", "total_estimado_estavel",
                       "percentual_realizado", "margem_erro", "margem_erro_percentual"],
            Linhas = p.Anos.Select(a => new object?[]
            {
                a.Ano.ToString(), (long)a.MesesRealizados, (long)a.MesesSemDados, (double)a.Realizado, (double)Math.Round(a.TotalTendencia, 2),
                (double)Math.Round(a.TotalEstavel, 2), a.PercentualRealizado is decimal pr ? (double)Math.Round(pr, 2) : null,
                a.MargemErro is decimal me ? (double)me : null, a.MargemPercentual is decimal mp ? (double)Math.Round(mp, 2) : null
            }).ToList()
        };
        var e = p.Estatistica;
        var estatistica = new ResultadoConsulta
        {
            Id = "projecao_estatistica",
            Objetivo = $"{metrica}: estudo estatístico das variações mensais (média, desvio padrão, margem de erro de 95%)",
            Sql = "(calculado pelo sistema a partir do histórico)",
            Colunas = ["indicador", "valor"],
            Linhas = e is null ? [] :
            [
                ["Meses comparados (amostra)", e.Meses.ToString()],
                ["Variação média mensal (média da amostra)", Fmt.Pct(e.Media)],
                ["Desvio padrão das variações", $"{e.DesvioPadrao.ToString("N2", Fmt.PtBr)} p.p."],
                ["Taxa adotada na projeção (mediana)", Fmt.Pct(e.Mediana)],
                ["Erro padrão da média", $"{e.ErroPadrao.ToString("N2", Fmt.PtBr)} p.p."],
                [$"Margem de erro ({e.Confianca}% de confiança)", $"± {e.Margem.ToString("N2", Fmt.PtBr)} p.p."]
            ]
        };
        return (mensal, anual, estatistica);
    }
}
