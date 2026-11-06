using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace RelatorioIA.Api.Services.Consultas;

/// <summary>
/// "Views" que a IA consulta — montadas como CTEs sobre as views do painel ([Tcudb].[dbo].[vw_DespesaCompleta] e
/// [vw_ReceitaCompleta]); o usuário do banco não pode criar views, então elas existem só dentro de cada consulta.
/// As regras de negócio ficam AQUI (e não no SQL da IA):
///  - vw_despesas.valor_empenhado_consolidado = Empenhado + Reforço − Anulação (por linha)
///  - vw_despesas.valor_liquidado / valor_pago = apenas as linhas desses tipos
///  - vw_receitas.valor inclui as deduções (negativas)
///  - textos com collation Latin1_General_CI_AI: LIKE/igualdade ignoram acentos e maiúsculas
///    ('%PATRIMONIO%' encontra "Patrimônio"); o banco usa CI_AS (sensível a acento).
///  - municipio = código do banco (ex.: 'aguas-da-prata').
/// </summary>
public class EsquemaConsulta(IConfiguration cfg, IMemoryCache cache)
{
    public static readonly string[] ViewsPermitidas = ["vw_despesas", "vw_receitas"];

    const string CI = "COLLATE Latin1_General_CI_AI";

    const string MesNome =
        "CASE MesReferencia WHEN 1 THEN N'JANEIRO' WHEN 2 THEN N'FEVEREIRO' WHEN 3 THEN N'MARÇO' WHEN 4 THEN N'ABRIL' " +
        "WHEN 5 THEN N'MAIO' WHEN 6 THEN N'JUNHO' WHEN 7 THEN N'JULHO' WHEN 8 THEN N'AGOSTO' WHEN 9 THEN N'SETEMBRO' " +
        "WHEN 10 THEN N'OUTUBRO' WHEN 11 THEN N'NOVEMBRO' WHEN 12 THEN N'DEZEMBRO' END";

    static string SemCodigo(string col) =>
        $"(CASE WHEN CHARINDEX(N' - ', {col}) > 1 AND LEFT({col}, CHARINDEX(N' - ', {col}) - 1) NOT LIKE N'%[^0-9]%' " +
        $"THEN SUBSTRING({col}, CHARINDEX(N' - ', {col}) + 3, 400) ELSE {col} END) {CI}";

    /// <summary>Definição das CTEs (sem a palavra WITH), para prefixar o SQL da IA.</summary>
    public static readonly string Ctes = $"""
        vw_despesas AS (
            SELECT Municipio AS municipio, Ano AS ano, MesReferencia AS mes, {MesNome} AS mes_nome,
                   Orgao {CI} AS orgao, TipoDespesa {CI} AS tipo_despesa, NumeroEmpenho {CI} AS nr_empenho,
                   DescricaoDespesa {CI} AS credor, IdentificadorDespesa {CI} AS identificador_credor,
                   CAST(DataEmissao AS date) AS data_emissao,
                   FuncaoGoverno {CI} AS funcao, SubfuncaoGoverno {CI} AS subfuncao,
                   CodigoPrograma {CI} AS cd_programa, DescricaoPrograma {CI} AS programa,
                   CodigoAcao {CI} AS cd_acao, DescricaoAcao {CI} AS acao,
                   FonteRecurso {CI} AS fonte_recurso, CodigoAplicacao {CI} AS codigo_aplicacao,
                   ModalidadeLicitacao {CI} AS modalidade_licitacao, Elemento {CI} AS elemento_despesa,
                   HistoricoDespesa {CI} AS historico,
                   ValorDespesa AS valor,
                   CASE WHEN TipoDespesa IN (N'Empenhado', N'Reforço') THEN ValorDespesa
                        WHEN TipoDespesa = N'Anulação' THEN -ValorDespesa ELSE 0 END AS valor_empenhado_consolidado,
                   CASE WHEN TipoDespesa = N'Valor Liquidado' THEN ValorDespesa ELSE 0 END AS valor_liquidado,
                   CASE WHEN TipoDespesa = N'Valor Pago' THEN ValorDespesa ELSE 0 END AS valor_pago
            FROM dbo.vw_DespesaCompleta),
        vw_receitas AS (
            SELECT Municipio AS municipio, Ano AS ano, MesReferencia AS mes, {MesNome} AS mes_nome,
                   Orgao {CI} AS orgao, DsPoder {CI} AS poder,
                   {SemCodigo("FonteRecurso")} AS fonte_recurso,
                   {SemCodigo("CategoriaReceita")} AS categoria, SubCategoriaReceita {CI} AS subcategoria,
                   DsFonte {CI} AS origem, DsRubrica {CI} AS rubrica, DsAlinea {CI} AS alinea,
                   DsSubAlinea {CI} AS subalinea, DsTipo {CI} AS detalhamento,
                   DsAplicacaoFixa {CI} AS codigo_aplicacao, ValorReceita AS valor
            FROM dbo.vw_ReceitaCompleta)
        """;

    /// <summary>Valores distintos das colunas categóricas — vão no prompt para a IA filtrar com o texto exato. Cache de 30 min.</summary>
    public async Task<string> ValoresDistintosAsync(CancellationToken ct = default) =>
        (await cache.GetOrCreateAsync("valores-distintos", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            return await MontarValoresAsync(ct);
        }))!;

    /// <summary>Recalcula e troca no cache (em segundo plano, antes de expirar).</summary>
    public async Task AtualizarAsync(CancellationToken ct = default) =>
        cache.Set("valores-distintos", await MontarValoresAsync(ct), TimeSpan.FromMinutes(30));

    async Task<string> MontarValoresAsync(CancellationToken ct)
    {
        var colunas = new (string view, string col)[]
        {
            ("vw_despesas", "orgao"), ("vw_despesas", "tipo_despesa"), ("vw_despesas", "funcao"), ("vw_despesas", "subfuncao"),
            ("vw_despesas", "fonte_recurso"), ("vw_despesas", "modalidade_licitacao"),
            ("vw_receitas", "orgao"), ("vw_receitas", "fonte_recurso"), ("vw_receitas", "categoria")
        };
        // Só os anos recentes (a classificação contábil mudou ao longo dos anos; os textos antigos confundem a IA)
        var anoMin = DateTime.Today.Year - 2;
        var sb = new StringBuilder();
        await using var con = new SqlConnection(cfg.GetConnectionString("Tcudb"));
        await con.OpenAsync(ct);
        foreach (var (view, col) in colunas)
        {
            await using var cmd = con.CreateCommand();
            cmd.CommandText = $"WITH {Ctes} SELECT DISTINCT TOP 60 {col} FROM {view} WHERE ano >= {anoMin} AND {col} <> N'' ORDER BY 1";
            cmd.CommandTimeout = 60;
            var valores = new List<string>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) if (!rd.IsDBNull(0)) valores.Add(rd.GetString(0));
            sb.AppendLine($"- {view}.{col}: {string.Join(" | ", valores.Select(v => $"'{v}'"))}");
        }
        return sb.ToString();
    }
}
