namespace RelatorioIA.Api.Data;

/// <summary>Linha de [Tcudb].[dbo].[vw_DespesaCompleta] (layout de despesas do TCESP). Somente leitura.</summary>
public class Despesa
{
    public long Id { get; set; }                       // IdDespesaCompleta
    public int Ano { get; set; }
    public string? Municipio { get; set; }             // código: "aguas-da-prata"
    public string? Orgao { get; set; }
    public int Mes { get; set; }                       // MesReferencia
    public string? TipoDespesa { get; set; }           // Empenhado | Reforço | Anulação | Valor Liquidado | Valor Pago
    public string? NrEmpenho { get; set; }
    public string? IdentificadorDespesa { get; set; }
    public string? Credor { get; set; }                // DescricaoDespesa
    public DateTime? DataEmissao { get; set; }
    public decimal Valor { get; set; }                 // ValorDespesa
    public string? Funcao { get; set; }
    public string? Subfuncao { get; set; }
    public string? CdPrograma { get; set; }
    public string? Programa { get; set; }
    public string? CdAcao { get; set; }
    public string? Acao { get; set; }
    public string? FonteRecurso { get; set; }
    public string? CodAplicacao { get; set; }
    public string? Modalidade { get; set; }
    public string? Elemento { get; set; }
    public string? Historico { get; set; }
}

/// <summary>Linha de [Tcudb].[dbo].[vw_ReceitaCompleta] (layout de receitas do TCESP). Somente leitura.</summary>
public class Receita
{
    public long Id { get; set; }                       // IdReceitaCompleta
    public int Ano { get; set; }
    public string? Municipio { get; set; }             // código: "aguas-da-prata"
    public string? Orgao { get; set; }
    public int Mes { get; set; }                       // MesReferencia
    public string? Poder { get; set; }
    public string? FonteRecurso { get; set; }          // ex.: "01 - TESOURO"
    public string? CodAplicacaoFixo { get; set; }
    public string? CodAplicacaoVariavel { get; set; }
    public string? Categoria { get; set; }             // ex.: "10000000 - Receitas Correntes"
    public string? Subcategoria { get; set; }
    public string? Fonte { get; set; }                 // DsFonte
    public string? Rubrica { get; set; }               // DsRubrica (nível usado no relatório)
    public string? Alinea { get; set; }
    public string? Subalinea { get; set; }
    public string? Tipo { get; set; }
    public decimal Valor { get; set; }                 // ValorReceita (deduções vêm negativas)
}
