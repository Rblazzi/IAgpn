using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RelatorioIA.Api.Data;
using RelatorioIA.Api.Models;

namespace RelatorioIA.Api.Services;

/// <summary>
/// Calcula todos os números do relatório direto do banco. A IA nunca faz contas:
/// ela só interpreta o pedido e escreve os textos a partir destes valores.
///
/// Regras (conferidas contra o relatório gerencial de exemplo):
///  - Receita consolidada = soma de vl_arrecadacao (inclui deduções negativas).
///  - Despesa empenhada consolidada = Empenhado + Reforço − Anulação.
/// </summary>
public class CalculadoraRelatorio(DbContextOptions<RelatorioDbContext> opcoesDb, IMemoryCache cache, IConfiguration cfg)
{
    /// <summary>Um contexto por consulta: permite buscar receitas/despesas e os dois anos do comparativo em paralelo.</summary>
    RelatorioDbContext NovoContexto() => new(opcoesDb);

    const string Empenhado = "Empenhado", Reforco = "Reforço", Anulacao = "Anulação",
        Liquidado = "Valor Liquidado", Pago = "Valor Pago";

    /// <summary>
    /// Municipio = nome para exibição ("Águas da Prata"); Codigo = valor da coluna Municipio no banco ("aguas-da-prata").
    /// MesMaxReceita/MesMaxDespesa = último mês COMPLETO (todos os órgãos do município enviaram ao TCESP).
    /// MesParcial* = último mês com qualquer dado; Pendencias descreve os meses parciais e os órgãos que faltam.
    /// Ex.: Águas da Prata 2026 — a Prefeitura não enviou agosto, só Câmara e RPPS: despesas completas até julho.
    /// </summary>
    public record Disponibilidade(string Municipio, int Ano, int? MesMaxReceita, int? MesMaxDespesa,
        int? MesParcialReceita = null, int? MesParcialDespesa = null, string? Pendencias = null, string Codigo = "");

    record OrgaoMes(string Municipio, int Ano, int Mes, string Orgao);

    static readonly TimeSpan TempoCache = TimeSpan.FromMinutes(10);

    /// <summary>O que existe no banco — usado para orientar a IA e validar pedidos. Em cache por 10 min (consulta pesada).</summary>
    public async Task<List<Disponibilidade>> CatalogoAsync(CancellationToken ct = default) =>
        (await cache.GetOrCreateAsync("catalogo", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TempoCache;
            return await MontarCatalogoAsync(ct);
        }))!;

    /// <summary>
    /// Recalcula o catálogo e troca o do cache (chamado em segundo plano antes de expirar — nenhum usuário espera por ele).
    /// </summary>
    public async Task AtualizarCatalogoAsync(CancellationToken ct = default) =>
        cache.Set("catalogo", await MontarCatalogoAsync(ct), TempoCache);

    async Task<List<Disponibilidade>> MontarCatalogoAsync(CancellationToken ct)
    {
        // Receitas e despesas em paralelo (cada uma com sua conexão)
        async Task<List<OrgaoMes>> LerReceitas()
        {
            await using var db = NovoContexto();
            var l = await db.Receitas.Select(r => new { r.Municipio, r.Ano, r.Mes, r.Orgao }).Distinct().ToListAsync(ct);
            return l.Select(r => new OrgaoMes(r.Municipio ?? "", r.Ano, r.Mes, r.Orgao ?? "")).ToList();
        }
        async Task<List<OrgaoMes>> LerDespesas()
        {
            await using var db = NovoContexto();
            var l = await db.Despesas.Select(r => new { r.Municipio, r.Ano, r.Mes, r.Orgao }).Distinct().ToListAsync(ct);
            return l.Select(r => new OrgaoMes(r.Municipio ?? "", r.Ano, r.Mes, r.Orgao ?? "")).ToList();
        }
        var tR = LerReceitas();
        var tD = LerDespesas();
        var linhasR = await tR;
        var linhasD = await tD;
        var nomes = linhasR.Concat(linhasD).GroupBy(l => l.Municipio)
            .ToDictionary(g => g.Key, g => NomeMunicipio(g.Key, g.Select(x => x.Orgao)));

        return linhasR.Select(r => (r.Municipio, r.Ano)).Union(linhasD.Select(d => (d.Municipio, d.Ano)))
            .Distinct().OrderBy(x => nomes[x.Municipio]).ThenBy(x => x.Ano)
            .Select(k =>
            {
                var (rc, rp, rf) = Completude(linhasR.Where(r => r.Municipio == k.Municipio && r.Ano == k.Ano).ToList());
                var (dc, dp, df) = Completude(linhasD.Where(r => r.Municipio == k.Municipio && r.Ano == k.Ano).ToList());
                var pend = new List<string>();
                if (rf != null) pend.Add($"receitas {rf}");
                if (df != null) pend.Add($"despesas {df}");
                return new Disponibilidade(nomes[k.Municipio], k.Ano, rc, dc, rp, dp, pend.Count > 0 ? string.Join("; ", pend) : null, k.Municipio);
            })
            .ToList();
    }

    /// <summary>
    /// Nome de exibição do município: 1) configuração "Municipios:{codigo}"; 2) derivado do órgão
    /// "PREFEITURA MUNICIPAL DE ÁGUAS DA PRATA" → "Águas da Prata"; 3) o próprio código formatado.
    /// </summary>
    string NomeMunicipio(string codigo, IEnumerable<string> orgaos)
    {
        if (cfg[$"Municipios:{codigo}"] is { Length: > 0 } configurado) return configurado;
        var pref = orgaos.FirstOrDefault(o => o.StartsWith("PREFEITURA MUNICIPAL D", StringComparison.OrdinalIgnoreCase));
        var bruto = pref is not null
            ? System.Text.RegularExpressions.Regex.Replace(pref, @"^PREFEITURA MUNICIPAL D[AEO]S?\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : codigo.Replace('-', ' ');
        return Fmt.NomeProprio(bruto);
    }

    /// <summary>Converte o nome de exibição (ou o próprio código) no código usado no banco.</summary>
    public async Task<string> CodigoAsync(string municipio, CancellationToken ct = default)
    {
        var cat = await CatalogoAsync(ct);
        return cat.FirstOrDefault(c => string.Equals(c.Municipio, municipio, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(c.Codigo, municipio, StringComparison.OrdinalIgnoreCase))?.Codigo ?? municipio;
    }

    /// <summary>
    /// Último mês completo: maior mês M tal que, em todos os meses 1..M, todos os órgãos "regulares" do ano
    /// (presentes em pelo menos metade dos meses) enviaram dados. Devolve também o último mês com qualquer dado
    /// e uma descrição das pendências (ex.: "ago a set/2026 parciais — falta: PREFEITURA ...").
    /// </summary>
    static (int? completo, int? parcial, string? pendencias) Completude(List<OrgaoMes> linhas)
    {
        if (linhas.Count == 0) return (null, null, null);
        var porMes = linhas.GroupBy(l => l.Mes).ToDictionary(g => g.Key, g => g.Select(x => x.Orgao).ToHashSet());
        var ultimo = porMes.Keys.Max();
        var regulares = linhas.GroupBy(l => l.Orgao).Where(g => g.Select(x => x.Mes).Distinct().Count() * 2 >= ultimo)
            .Select(g => g.Key).ToList();
        var completo = 0;
        for (var m = 1; m <= ultimo; m++)
        {
            if (!porMes.TryGetValue(m, out var orgs) || regulares.Any(o => !orgs.Contains(o))) break;
            completo = m;
        }
        if (completo == ultimo) return (completo, ultimo, null);
        var faltam = Enumerable.Range(completo + 1, ultimo - completo)
            .SelectMany(m => regulares.Where(o => !porMes.GetValueOrDefault(m, []).Contains(o))).Distinct();
        var ano = linhas[0].Ano;
        var meses = completo + 1 == ultimo ? Fmt.MesCurto(ultimo) : $"{Fmt.MesCurto(completo + 1)} a {Fmt.MesCurto(ultimo)}";
        return (completo == 0 ? null : completo, ultimo, $"{meses.ToLower(Fmt.PtBr)}/{ano} parciais — falta: {string.Join(", ", faltam)}");
    }

    record LinhaRec(string Orgao, int Mes, string FonteRecurso, string Categoria, string Rubrica, decimal Valor);
    record LinhaDes(string Orgao, int Mes, string Tipo, string Funcao, string Subfuncao, string FonteRecurso, string Modalidade, decimal Valor);

    // Os valores já vêm SOMADOS do banco por (órgão, mês, classificações): todas as contas do relatório são somas
    // dessas mesmas chaves, então o resultado é idêntico — com muito menos linhas trafegando (antes: cada empenho).

    async Task<List<LinhaRec>> ReceitasAsync(string codigo, int ano, int ini, int fim, CancellationToken ct)
    {
        await using var db = NovoContexto();
        var linhas = await db.Receitas
            .Where(r => r.Municipio == codigo && r.Ano == ano && r.Mes >= ini && r.Mes <= fim)
            .GroupBy(r => new { r.Orgao, r.Mes, r.FonteRecurso, r.Categoria, r.Rubrica })
            .Select(g => new { g.Key.Orgao, g.Key.Mes, g.Key.FonteRecurso, g.Key.Categoria, g.Key.Rubrica, Valor = g.Sum(x => x.Valor) })
            .ToListAsync(ct);
        return linhas.Select(r => new LinhaRec(r.Orgao ?? "", r.Mes, r.FonteRecurso ?? "", r.Categoria ?? "", r.Rubrica ?? "", r.Valor)).ToList();
    }

    async Task<List<LinhaDes>> DespesasAsync(string codigo, int ano, int ini, int fim, CancellationToken ct)
    {
        await using var db = NovoContexto();
        var linhas = await db.Despesas
            .Where(r => r.Municipio == codigo && r.Ano == ano && r.Mes >= ini && r.Mes <= fim)
            .GroupBy(r => new { r.Orgao, r.Mes, r.TipoDespesa, r.Funcao, r.Subfuncao, r.FonteRecurso, r.Modalidade })
            .Select(g => new { g.Key.Orgao, g.Key.Mes, g.Key.TipoDespesa, g.Key.Funcao, g.Key.Subfuncao, g.Key.FonteRecurso, g.Key.Modalidade, Valor = g.Sum(x => x.Valor) })
            .ToListAsync(ct);
        return linhas.Select(r => new LinhaDes(r.Orgao ?? "", r.Mes, r.TipoDespesa ?? "", r.Funcao ?? "", r.Subfuncao ?? "",
            r.FonteRecurso ?? "", r.Modalidade ?? "", r.Valor)).ToList();
    }

    /// <summary>Valor que entra na despesa consolidada (anulação subtrai; liquidado/pago não entram).</summary>
    static decimal Consolidado(LinhaDes d) => d.Tipo switch
    {
        Empenhado or Reforco => d.Valor,
        Anulacao => -d.Valor,
        _ => 0m
    };

    static MatrizMensal Matriz<T>(IEnumerable<T> linhas, int[] meses, Func<T, string> rotulo, Func<T, int> mes, Func<T, decimal> valor)
    {
        var m = new MatrizMensal
        {
            Meses = meses,
            Linhas = linhas.GroupBy(rotulo)
                .Select(g => new LinhaMensal
                {
                    Rotulo = g.Key,
                    PorMes = g.GroupBy(mes).ToDictionary(x => x.Key, x => x.Sum(valor))
                })
                .Where(l => l.PorMes.Values.Any(v => v != 0))
                .OrderByDescending(l => l.Total).ToList()
        };
        return m;
    }

    public async Task<DadosRelatorio> CalcularAsync(ParametrosRelatorio p, CancellationToken ct = default)
    {
        var dados = new DadosRelatorio { Parametros = p };
        var meses = dados.Meses;

        // As 4 leituras (receitas/despesas do ano e do ano base do comparativo) em paralelo
        var codigo = await CodigoAsync(p.Municipio, ct);
        var tRec = ReceitasAsync(codigo, p.Ano, p.MesInicial, p.MesFinal, ct);
        var tDes = DespesasAsync(codigo, p.Ano, p.MesInicial, p.MesFinal, ct);
        Task<List<LinhaRec>>? tRecBase = null;
        Task<List<LinhaDes>>? tDesBase = null;
        if (p.AnoComparacao is int ab)
        {
            tRecBase = ReceitasAsync(codigo, ab, p.MesInicial, p.MesFinal, ct);
            tDesBase = DespesasAsync(codigo, ab, p.MesInicial, p.MesFinal, ct);
        }
        var rec = await tRec;
        var des = await tDes;

        foreach (var m in meses)
        {
            if (p.IncluirReceitas && !rec.Any(r => r.Mes == m)) dados.Avisos.Add($"Sem dados de receita para {Fmt.MesTitulo(m)}/{p.Ano}.");
            if (p.IncluirDespesas && !des.Any(r => r.Mes == m)) dados.Avisos.Add($"Sem dados de despesa para {Fmt.MesTitulo(m)}/{p.Ano}.");
        }

        if (p.IncluirReceitas && rec.Count > 0)
        {
            dados.Receita = new DadosReceita
            {
                Total = rec.Sum(r => r.Valor),
                PorMes = rec.GroupBy(r => r.Mes).ToDictionary(g => g.Key, g => g.Sum(r => r.Valor)),
                PorFonte = Matriz(rec, meses, r => Fmt.SemCodigo(r.FonteRecurso), r => r.Mes, r => r.Valor),
                PorOrgao = Matriz(rec, meses, r => r.Orgao, r => r.Mes, r => r.Valor),
                PorCategoria = Matriz(rec, meses, r => Fmt.SemCodigo(r.Categoria), r => r.Mes, r => r.Valor),
                RubricasPorMes = meses.Select(m =>
                {
                    var doMes = rec.Where(r => r.Mes == m).ToList();
                    var top = doMes.GroupBy(r => r.Rubrica).Select(g => new ItemValor(g.Key, g.Sum(x => x.Valor)))
                        .OrderByDescending(x => x.Valor).Take(6).ToList();
                    return new RubricasMes(m, top, doMes.Sum(x => x.Valor));
                }).Where(x => x.Top.Count > 0).ToList(),
                PrincipaisRubricasPeriodo = rec.GroupBy(r => r.Rubrica)
                    .Select(g => new ItemValor(g.Key, g.Sum(x => x.Valor)))
                    .OrderByDescending(x => x.Valor).Take(6).ToList()
            };
        }

        if (p.IncluirDespesas && des.Count > 0)
        {
            var cons = des.Where(d => d.Tipo is Empenhado or Reforco or Anulacao).ToList();
            var porFuncao = cons.GroupBy(d => d.Funcao).Select(g => new ItemValor(g.Key, g.Sum(Consolidado)))
                .Where(x => x.Valor != 0).OrderByDescending(x => x.Valor).ToList();

            dados.Despesa = new DadosDespesa
            {
                Empenhado = des.Where(d => d.Tipo == Empenhado).Sum(d => d.Valor),
                Reforco = des.Where(d => d.Tipo == Reforco).Sum(d => d.Valor),
                Anulacao = des.Where(d => d.Tipo == Anulacao).Sum(d => d.Valor),
                Liquidado = des.Where(d => d.Tipo == Liquidado).Sum(d => d.Valor),
                Pago = des.Where(d => d.Tipo == Pago).Sum(d => d.Valor),
                ConsolidadaPorMes = cons.GroupBy(d => d.Mes).ToDictionary(g => g.Key, g => g.Sum(Consolidado)),
                PorModalidade = Matriz(cons, meses, d => d.Modalidade, d => d.Mes, Consolidado),
                PorOrgao = Matriz(cons, meses, d => d.Orgao, d => d.Mes, Consolidado),
                PorFonte = Matriz(cons, meses, d => d.FonteRecurso, d => d.Mes, Consolidado),
                PorFuncao = porFuncao,
                PorSubfuncao = cons.GroupBy(d => d.Subfuncao).Select(g => new ItemValor(g.Key, g.Sum(Consolidado)))
                    .Where(x => x.Valor != 0).OrderByDescending(x => x.Valor).ToList(),
                FuncoesDetalhadas = porFuncao.Take(5).Select(f => new FuncaoDetalhe(f.Rotulo,
                    Matriz(cons.Where(d => d.Funcao == f.Rotulo), meses, d => d.Subfuncao, d => d.Mes, Consolidado))).ToList()
            };
        }

        if (p.AnoComparacao is int anoBase)
            dados.Comparativo = Comparar(anoBase, await tRecBase!, await tDesBase!, rec, des, dados.Avisos);

        return dados;
    }

    static DadosComparativo? Comparar(int anoBase, List<LinhaRec> recBase, List<LinhaDes> desBase, List<LinhaRec> recAtual,
        List<LinhaDes> desAtual, List<string> avisos)
    {
        if (recBase.Count == 0 && desBase.Count == 0)
        {
            avisos.Add($"Não há dados de {anoBase} no banco para o comparativo.");
            return null;
        }

        static List<ItemComparativo> Cruzar<T>(IEnumerable<T> b, IEnumerable<T> a, Func<T, string> chave, Func<T, decimal> valor)
        {
            var db = b.GroupBy(chave).ToDictionary(g => g.Key, g => g.Sum(valor));
            var da = a.GroupBy(chave).ToDictionary(g => g.Key, g => g.Sum(valor));
            return db.Keys.Union(da.Keys)
                .Select(k => new ItemComparativo(k, db.GetValueOrDefault(k), da.GetValueOrDefault(k)))
                .Where(x => x.Base != 0 || x.Atual != 0)
                .OrderByDescending(x => x.Atual).ToList();
        }

        var consBase = desBase.Where(d => d.Tipo is Empenhado or Reforco or Anulacao).ToList();
        var consAtual = desAtual.Where(d => d.Tipo is Empenhado or Reforco or Anulacao).ToList();
        var pagoBase = desBase.Where(d => d.Tipo == Pago).ToList();
        var pagoAtual = desAtual.Where(d => d.Tipo == Pago).ToList();

        var pagoPorFuncao = Cruzar(pagoBase, pagoAtual, d => d.Funcao, d => d.Valor);
        // Detalha por subfunção as funções com maior variação absoluta (como no relatório de exemplo)
        var detalhar = pagoPorFuncao.Where(x => x.Base != 0).OrderByDescending(x => Math.Abs(x.Diferenca)).Take(6);

        return new DadosComparativo
        {
            AnoBase = anoBase,
            ReceitaBase = recBase.Sum(r => r.Valor),
            DespesaBase = consBase.Sum(Consolidado),
            PagoBase = pagoBase.Sum(d => d.Valor),
            ReceitaBasePorMes = recBase.GroupBy(r => r.Mes).ToDictionary(g => g.Key, g => g.Sum(r => r.Valor)),
            DespesaBasePorMes = consBase.GroupBy(r => r.Mes).ToDictionary(g => g.Key, g => g.Sum(Consolidado)),
            ReceitaPorFonte = Cruzar(recBase, recAtual, r => Fmt.SemCodigo(r.FonteRecurso), r => r.Valor),
            ReceitaPorOrgao = Cruzar(recBase, recAtual, r => r.Orgao, r => r.Valor),
            DespesaPorFuncao = Cruzar(consBase, consAtual, d => d.Funcao, Consolidado),
            PagoPorFuncao = pagoPorFuncao,
            PagoDetalhado = detalhar.Select(f => new FuncaoComparativoDetalhe(f.Rotulo,
                Cruzar(pagoBase.Where(d => d.Funcao == f.Rotulo), pagoAtual.Where(d => d.Funcao == f.Rotulo),
                    d => d.Subfuncao, d => d.Valor))).ToList()
        };
    }
}
