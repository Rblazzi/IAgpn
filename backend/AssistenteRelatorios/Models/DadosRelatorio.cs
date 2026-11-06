using System.Globalization;

namespace RelatorioIA.Api.Models;

/// <summary>Parâmetros do relatório, extraídos do pedido do usuário (pela IA ou pelo parser local).</summary>
public record ParametrosRelatorio(string Municipio, int Ano, int MesInicial, int MesFinal, int? AnoComparacao,
    bool IncluirReceitas = true, bool IncluirDespesas = true);

/// <summary>Uma tabela "rótulo x mês" com valores; total e % são calculados.</summary>
public class MatrizMensal
{
    public required int[] Meses { get; init; }
    public List<LinhaMensal> Linhas { get; init; } = [];
    public decimal Total => Linhas.Sum(l => l.Total);
    public decimal TotalMes(int mes) => Linhas.Sum(l => l.Mes(mes));
}

public class LinhaMensal
{
    public required string Rotulo { get; init; }
    public Dictionary<int, decimal> PorMes { get; init; } = [];
    public decimal Mes(int m) => PorMes.GetValueOrDefault(m);
    public decimal Total => PorMes.Values.Sum();
}

public record ItemValor(string Rotulo, decimal Valor);
public record ItemComparativo(string Rotulo, decimal Base, decimal Atual)
{
    public decimal Diferenca => Atual - Base;
    public decimal? Percentual => Base == 0 ? null : Diferenca / Base * 100m;
}
public record RubricasMes(int Mes, List<ItemValor> Top, decimal TotalMes);
public record FuncaoDetalhe(string Funcao, MatrizMensal PorSubfuncao);
public record FuncaoComparativoDetalhe(string Funcao, List<ItemComparativo> Subfuncoes);

public class DadosReceita
{
    public decimal Total { get; set; }
    public required MatrizMensal PorFonte { get; init; }
    public required MatrizMensal PorOrgao { get; init; }
    public required MatrizMensal PorCategoria { get; init; }
    public List<RubricasMes> RubricasPorMes { get; init; } = [];
    public List<ItemValor> PrincipaisRubricasPeriodo { get; init; } = [];
    public Dictionary<int, decimal> PorMes { get; init; } = [];
}

public class DadosDespesa
{
    public decimal Empenhado { get; set; }
    public decimal Reforco { get; set; }
    public decimal Anulacao { get; set; }
    public decimal Consolidada => Empenhado + Reforco - Anulacao;
    public decimal Liquidado { get; set; }
    public decimal Pago { get; set; }
    public Dictionary<int, decimal> ConsolidadaPorMes { get; init; } = [];
    public required MatrizMensal PorModalidade { get; init; }
    public required MatrizMensal PorOrgao { get; init; }
    public required MatrizMensal PorFonte { get; init; }
    public List<ItemValor> PorFuncao { get; init; } = [];
    public List<ItemValor> PorSubfuncao { get; init; } = [];
    public List<FuncaoDetalhe> FuncoesDetalhadas { get; init; } = [];
}

public class DadosComparativo
{
    public int AnoBase { get; init; }
    public Dictionary<int, decimal> ReceitaBasePorMes { get; init; } = [];
    public Dictionary<int, decimal> DespesaBasePorMes { get; init; } = [];
    public decimal ReceitaBase { get; init; }
    public decimal DespesaBase { get; init; }
    public decimal PagoBase { get; init; }
    public List<ItemComparativo> ReceitaPorFonte { get; init; } = [];
    public List<ItemComparativo> ReceitaPorOrgao { get; init; } = [];
    public List<ItemComparativo> DespesaPorFuncao { get; init; } = [];
    public List<ItemComparativo> PagoPorFuncao { get; init; } = [];
    public List<FuncaoComparativoDetalhe> PagoDetalhado { get; init; } = [];
}

public class DadosRelatorio
{
    public required ParametrosRelatorio Parametros { get; init; }
    public int[] Meses => Enumerable.Range(Parametros.MesInicial, Parametros.MesFinal - Parametros.MesInicial + 1).ToArray();
    public DadosReceita? Receita { get; set; }
    public DadosDespesa? Despesa { get; set; }
    public DadosComparativo? Comparativo { get; set; }
    public List<string> Avisos { get; } = [];
    /// <summary>Pontos de atenção calculados pelo sistema (indicadores automáticos, sem IA). Null = não calculados.</summary>
    public PontosAtencao? Pontos { get; set; }
}

/// <summary>Um indicador que passou do limite: o que é, o detalhe (com os números) e o valor principal.</summary>
public record PontoAtencao(string Ponto, string Detalhe, string Valor);

/// <summary>
/// Indicadores automáticos para acompanhamento da gestão (não indicam irregularidade): saltos de credores e elementos
/// de despesa, concentração, dispensa de licitação, empenhos no fim do mês e execução financeira.
/// </summary>
public class PontosAtencao
{
    public List<PontoAtencao> Itens { get; } = [];
    /// <summary>Credores/elementos com maior salto (base × atual) — vira o gráfico da seção.</summary>
    public List<ItemComparativo> Saltos { get; } = [];
    public int? AnoBase { get; init; }
}

public static class Fmt
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    static readonly string[] NomesMes = ["JANEIRO", "FEVEREIRO", "MARÇO", "ABRIL", "MAIO", "JUNHO", "JULHO", "AGOSTO", "SETEMBRO", "OUTUBRO", "NOVEMBRO", "DEZEMBRO"];

    public static string Moeda(decimal v) => v < 0 ? $"R$ ({(-v).ToString("N2", PtBr)})" : $"R$ {v.ToString("N2", PtBr)}";
    public static string Pct(decimal? v, int casas = 2) => v is null ? "***" : v.Value.ToString("N" + casas, PtBr) + "%";
    public static string Mes(int m) => NomesMes[m - 1];
    public static string MesCurto(int m) => NomesMes[m - 1][..3];
    public static string MesTitulo(int m) => PtBr.TextInfo.ToTitleCase(NomesMes[m - 1].ToLower(PtBr));
    public static string Periodo(int ini, int fim) => ini == fim ? Mes(ini) : $"{MesCurto(ini)} A {Mes(fim)}";
    public static string PeriodoExtenso(int ini, int fim) => ini == fim ? MesTitulo(ini) : $"{MesTitulo(ini)} a {MesTitulo(fim)}";

    /// <summary>"ÁGUAS DA PRATA" → "Águas da Prata" (preposições em minúsculas).</summary>
    public static string NomeProprio(string s)
    {
        string[] minusculas = ["da", "das", "de", "do", "dos", "e"];
        var palavras = s.Trim().ToLower(PtBr).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((p, i) => i > 0 && minusculas.Contains(p) ? p : char.ToUpper(p[0], PtBr) + p[1..]);
        return string.Join(' ', palavras);
    }

    /// <summary>"01 - TESOURO" → "TESOURO"; "10000000 - Receitas Correntes" → "Receitas Correntes".</summary>
    public static string SemCodigo(string s)
    {
        var i = s.IndexOf(" - ", StringComparison.Ordinal);
        return i > 0 && s[..i].All(char.IsDigit) ? s[(i + 3)..].Trim() : s.Trim();
    }
}
