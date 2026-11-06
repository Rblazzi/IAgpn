using RelatorioIA.Api.Models;

namespace RelatorioIA.Api.Services.Consultas;

/// <summary>
/// "Pontos de atenção" do relatório gerencial: indicadores calculados pelo SISTEMA (SQL fixo, sem IA), com limites
/// definidos aqui. Servem para orientar o acompanhamento da gestão — não indicam irregularidade.
/// Folha de pagamento e previdência (credor = Prefeitura, Câmara, Instituto/INSS) ficam fora dos indicadores de credor.
/// </summary>
public class IndicadoresAtencao(ExecutorSql sql, ILogger<IndicadoresAtencao> log)
{
    // Limites dos indicadores
    const decimal CrescimentoCredor = 100m;      // % de aumento no pago
    const decimal CrescimentoElemento = 50m;     // % de aumento no empenhado
    const decimal ParticipacaoMinima = 0.01m;    // diferença ≥ 1% do total do período para entrar
    const decimal ConcentracaoTop5 = 50m;        // % do pago nos 5 maiores credores
    const decimal Dispensa = 30m;                // % do empenhado por dispensa de licitação
    const decimal FimDoMes = 35m;                // % do empenhado emitido nos 3 últimos dias dos meses
    const decimal ExecucaoMinima = 70m;          // pago / empenhado

    const string SemFolha =
        "credor NOT LIKE '%PREFEITURA%' AND credor NOT LIKE '%CAMARA%' AND credor NOT LIKE '%INSTITUTO DE PREV%' " +
        "AND credor NOT LIKE '%INSS%' AND credor NOT LIKE '%PREVIDENCIA%' AND credor NOT LIKE '%FOLHA%'";
    // Pessoal e encargos (elementos 31.xx) e folha são empenhados no fim do mês por natureza: fora do indicador de fim de mês
    const string SemPessoal = SemFolha + " AND elemento_despesa NOT LIKE '31%'";

    public async Task<PontosAtencao?> CalcularAsync(ParametrosRelatorio p, string codigo, CancellationToken ct)
    {
        try
        {
            var mun = codigo.Replace("'", "");
            string Periodo(int ano) => $"municipio = '{mun}' AND ano = {ano} AND mes BETWEEN {p.MesInicial} AND {p.MesFinal}";
            var b = p.AnoComparacao;
            var r = new PontosAtencao { AnoBase = b };

            // Consultas independentes em paralelo (cada uma abre a própria conexão)
            var tTotais = sql.ExecutarAsync("pa_totais", "Totais", $"""
                SELECT SUM(valor_empenhado_consolidado) AS empenhado, SUM(valor_pago) AS pago,
                       SUM(CASE WHEN modalidade_licitacao LIKE '%DISPENSA%' THEN valor_empenhado_consolidado ELSE 0 END) AS dispensa,
                       SUM(CASE WHEN data_emissao IS NOT NULL AND DAY(data_emissao) >= DAY(EOMONTH(data_emissao)) - 2
                                 AND {SemPessoal} THEN valor_empenhado_consolidado ELSE 0 END) AS fim_mes,
                       SUM(CASE WHEN data_emissao IS NOT NULL AND {SemPessoal} THEN valor_empenhado_consolidado ELSE 0 END) AS com_data
                FROM vw_despesas WHERE {Periodo(p.Ano)}
                """, ct);
            var tTop = sql.ExecutarAsync("pa_top", "Concentração", $"""
                SELECT TOP 5 credor, SUM(valor_pago) AS pago,
                       (SELECT SUM(valor_pago) FROM vw_despesas WHERE {Periodo(p.Ano)} AND {SemFolha}) AS total
                FROM vw_despesas WHERE {Periodo(p.Ano)} AND {SemFolha}
                GROUP BY credor ORDER BY pago DESC
                """, ct);
            Task<ResultadoConsulta>? tCred = null, tElem = null;
            if (b is int ab)
            {
                tCred = sql.ExecutarAsync("pa_credores", "Credores", $"""
                    SELECT TOP 30 credor,
                           SUM(CASE WHEN ano = {ab} THEN valor_pago ELSE 0 END) AS base,
                           SUM(CASE WHEN ano = {p.Ano} THEN valor_pago ELSE 0 END) AS atual
                    FROM vw_despesas
                    WHERE municipio = '{mun}' AND ano IN ({ab}, {p.Ano}) AND mes BETWEEN {p.MesInicial} AND {p.MesFinal} AND {SemFolha}
                    GROUP BY credor
                    ORDER BY SUM(CASE WHEN ano = {p.Ano} THEN valor_pago ELSE 0 END) - SUM(CASE WHEN ano = {ab} THEN valor_pago ELSE 0 END) DESC
                    """, ct);
                tElem = sql.ExecutarAsync("pa_elementos", "Elementos", $"""
                    SELECT TOP 30 elemento_despesa,
                           SUM(CASE WHEN ano = {ab} THEN valor_empenhado_consolidado ELSE 0 END) AS base,
                           SUM(CASE WHEN ano = {p.Ano} THEN valor_empenhado_consolidado ELSE 0 END) AS atual
                    FROM vw_despesas
                    WHERE municipio = '{mun}' AND ano IN ({ab}, {p.Ano}) AND mes BETWEEN {p.MesInicial} AND {p.MesFinal}
                    GROUP BY elemento_despesa
                    ORDER BY SUM(CASE WHEN ano = {p.Ano} THEN valor_empenhado_consolidado ELSE 0 END) - SUM(CASE WHEN ano = {ab} THEN valor_empenhado_consolidado ELSE 0 END) DESC
                    """, ct);
            }
            await Task.WhenAll(new[] { tTotais, tTop, tCred, tElem }.OfType<Task<ResultadoConsulta>>());

            static decimal D(object? v) => ExecutorSql.ParaDecimal(v);
            static decimal Pct(decimal parte, decimal total) => total == 0 ? 0 : parte / total * 100m;

            var tot = tTotais.Result;
            if (!tot.Ok || tot.Linhas.Count == 0) { log.LogWarning("Pontos de atenção: totais indisponíveis ({Erro})", tot.Erro); return null; }
            var (empenhado, pago, dispensa, fimMes, comData) =
                (D(tot.Linhas[0][0]), D(tot.Linhas[0][1]), D(tot.Linhas[0][2]), D(tot.Linhas[0][3]), D(tot.Linhas[0][4]));

            // 1) Credores com salto de pagamentos (mesmo período do ano base)
            if (tCred?.Result is { Ok: true } cred)
            {
                foreach (var l in cred.Linhas)
                {
                    var (nome, vb, va) = (Convert.ToString(l[0]) ?? "", D(l[1]), D(l[2]));
                    if (va - vb < pago * ParticipacaoMinima) continue;
                    var cresc = vb == 0 ? (decimal?)null : Pct(va - vb, vb);
                    if (cresc is not null && cresc < CrescimentoCredor) continue;
                    r.Saltos.Add(new ItemComparativo(nome, vb, va));
                    if (r.Saltos.Count >= 5) break;
                }
                foreach (var s in r.Saltos)
                    r.Itens.Add(new("Credor com aumento expressivo de pagamentos", s.Base == 0
                        ? $"{s.Rotulo}: sem pagamentos no mesmo período de {b}; {Fmt.Moeda(s.Atual)} pagos em {p.Ano}."
                        : $"{s.Rotulo}: de {Fmt.Moeda(s.Base)} ({b}) para {Fmt.Moeda(s.Atual)} ({p.Ano}).",
                        s.Percentual is { } pc ? Fmt.Pct(pc) : "novo"));
            }

            // 2) Elementos de despesa com salto
            if (tElem?.Result is { Ok: true } elem)
            {
                var n = 0;
                foreach (var l in elem.Linhas)
                {
                    var (nome, vb, va) = (Convert.ToString(l[0]) ?? "", D(l[1]), D(l[2]));
                    if (vb <= 0 || va - vb < empenhado * ParticipacaoMinima || Pct(va - vb, vb) < CrescimentoElemento) continue;
                    r.Itens.Add(new("Elemento de despesa com crescimento acima do usual",
                        $"{nome}: de {Fmt.Moeda(vb)} ({b}) para {Fmt.Moeda(va)} ({p.Ano}).", Fmt.Pct(Pct(va - vb, vb))));
                    if (r.Saltos.Count < 8) r.Saltos.Add(new ItemComparativo(nome, vb, va));
                    if (++n >= 3) break;
                }
            }

            // 3) Concentração nos 5 maiores credores (sem folha/previdência)
            if (tTop.Result is { Ok: true, Linhas.Count: > 0 } top)
            {
                var totalSemFolha = D(top.Linhas[0][2]);
                var soma = top.Linhas.Sum(l => D(l[1]));
                if (Pct(soma, totalSemFolha) >= ConcentracaoTop5)
                    r.Itens.Add(new("Concentração de pagamentos em poucos credores",
                        $"Os 5 maiores credores (sem folha e previdência) receberam {Fmt.Moeda(soma)} de {Fmt.Moeda(totalSemFolha)}.",
                        Fmt.Pct(Pct(soma, totalSemFolha))));
            }

            // 4) Dispensa de licitação
            if (Pct(dispensa, empenhado) >= Dispensa)
                r.Itens.Add(new("Participação elevada de dispensa de licitação",
                    $"{Fmt.Moeda(dispensa)} empenhados por dispensa de licitação, de {Fmt.Moeda(empenhado)} no período.",
                    Fmt.Pct(Pct(dispensa, empenhado))));

            // 5) Empenhos concentrados nos últimos dias dos meses
            if (comData > 0 && Pct(fimMes, comData) >= FimDoMes)
                r.Itens.Add(new("Empenhos concentrados no fim dos meses",
                    $"{Fmt.Moeda(fimMes)} de {Fmt.Moeda(comData)} empenhados nos 3 últimos dias dos meses, sem folha e encargos de pessoal " +
                    "(em um mês típico, esses dias representam cerca de 10%).",
                    Fmt.Pct(Pct(fimMes, comData))));

            // 6) Execução financeira (pago × empenhado)
            if (empenhado > 0 && Pct(pago, empenhado) < ExecucaoMinima)
                r.Itens.Add(new("Execução financeira abaixo do empenhado",
                    $"Pago {Fmt.Moeda(pago)} de {Fmt.Moeda(empenhado)} empenhados; a diferença tende a formar restos a pagar.",
                    Fmt.Pct(Pct(pago, empenhado))));

            log.LogInformation("Pontos de atenção: {N} indicador(es) acima do limite", r.Itens.Count);
            return r;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Não foi possível calcular os pontos de atenção");
            return null;
        }
    }
}
