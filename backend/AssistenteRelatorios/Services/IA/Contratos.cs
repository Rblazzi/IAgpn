using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelatorioIA.Api.Services.IA;

public record MensagemChat(string Papel, string Texto); // Papel: "user" | "assistant"

/// <summary>
/// Contrato mínimo com o modelo de IA: recebe um prompt de sistema + conversa e devolve JSON no formato do schema.
/// Para usar o "padrão Gemini" de outro sistema, basta implementar esta interface.
/// </summary>
public interface IModeloIA
{
    string Nome { get; }
    /// <param name="anexos">Arquivos enviados pelo usuário: vão junto da última mensagem (texto extraído ou o arquivo em si).</param>
    Task<JsonElement> GerarJsonAsync(string sistema, IEnumerable<(string papel, string texto)> conversa,
        object schema, double temperatura, CancellationToken ct, IReadOnlyList<AnexoConteudo>? anexos = null);
}

/// <summary>
/// Arquivo anexado pelo usuário, pronto para a IA: <see cref="Texto"/> (Word, Excel, CSV, TXT — conteúdo extraído)
/// ou <see cref="Dados"/> + <see cref="Mime"/> (PDF e imagens vão inteiros para o modelo ler).
/// </summary>
public record AnexoConteudo(string Id, string Nome, string? Texto, string? Mime, byte[]? Dados)
{
    public bool Binario => Dados is not null;
}

/// <summary>
/// Contexto salvo junto de cada relatório (arquivo .contexto): permite à IA alterar ou continuar o relatório
/// anterior da conversa sem perder o que já foi feito (consultas, seções, anexos).
/// </summary>
public class ContextoRelatorio
{
    public string Pedido { get; set; } = "";
    /// <summary>consulta | gerencial</summary>
    public string Tipo { get; set; } = "consulta";
    public string Titulo { get; set; } = "";
    public Plano? Plano { get; set; }
    public EstruturaRelatorio? Estrutura { get; set; }
    public Models.ParametrosRelatorio? Parametros { get; set; }
    public List<string> Anexos { get; set; } = [];
    /// <summary>Títulos das seções do documento como está agora (com recuo por nível) — para editar/remover.</summary>
    public List<string> Titulos { get; set; } = [];
    /// <summary>Conteúdos acrescentados depois, em edições ("acrescente os gastos com combustível").</summary>
    public List<ContextoRelatorio> Acrescimos { get; set; } = [];
}

/// <summary>Plano devolvido pelo Prompt 1 (o que fazer + consultas SQL).</summary>
public class Plano
{
    /// <summary>relatorio_gerencial | relatorio_consulta | responder | esclarecer</summary>
    public string Acao { get; set; } = "relatorio_gerencial";
    public string? Mensagem { get; set; }
    // relatório gerencial
    public string? Municipio { get; set; }
    public int? Ano { get; set; }
    public int? MesInicial { get; set; }
    public int? MesFinal { get; set; }
    public int? AnoComparacao { get; set; }
    public bool? IncluirReceitas { get; set; }
    public bool? IncluirDespesas { get; set; }
    // consultas (pode envolver vários municípios — comparativos entre cidades)
    public List<string> Municipios { get; set; } = [];
    public string? Titulo { get; set; }
    public string? PeriodoDescricao { get; set; }
    public List<ConsultaPlanejada> Consultas { get; set; } = [];
    /// <summary>Pedido de projeção/estimativa: o SISTEMA calcula a partir do histórico mensal da consulta indicada.</summary>
    public ProjecaoPlanejada? Projecao { get; set; }
    /// <summary>Quem assina na última página (ex.: "Gestor responsável"). Vazio = padrão da configuração.</summary>
    public string? Assinatura { get; set; }
    /// <summary>editar_relatorio: títulos das seções do relatório atual a remover.</summary>
    public List<string> RemoverSecoes { get; set; } = [];
    /// <summary>editar_relatorio: títulos das seções a ALTERAR (só elas mudam, no mesmo lugar; o resto fica igual).</summary>
    public List<string> AlterarSecoes { get; set; } = [];
    /// <summary>
    /// Alterar seções: true SÓ quando o usuário pediu para trocar a tabela/gráfico/dados mostrados na seção. Senão as
    /// tabelas e gráficos atuais são mantidos e as consultas novas servem apenas de base para o texto.
    /// </summary>
    public bool TrocarTabelas { get; set; }
}

public class ProjecaoPlanejada
{
    public string ConsultaId { get; set; } = "";
    public string? CampoValor { get; set; }
    public int AteAno { get; set; }
    public int AteMes { get; set; } = 12;
    /// <summary>O que está sendo projetado, ex.: "Despesa empenhada com Saúde".</summary>
    public string Metrica { get; set; } = "";
    /// <summary>Simulação "e se" pedida pelo usuário (ex.: ICMS cai 5% no próximo trimestre). O sistema calcula.</summary>
    public SimulacaoPlanejada? Simulacao { get; set; }
}

/// <summary>Cenário simulado: variação de X% sobre a tendência, nos meses projetados do intervalo (vazio = todos).</summary>
public class SimulacaoPlanejada
{
    public decimal Percentual { get; set; }
    public int? DeAno { get; set; }
    public int? DeMes { get; set; }
    public int? AteAno { get; set; }
    public int? AteMes { get; set; }
    /// <summary>Como o usuário descreveu, ex.: "queda de 5% no ICMS no próximo trimestre".</summary>
    public string Descricao { get; set; } = "";
}

public class ConsultaPlanejada
{
    public string Id { get; set; } = "";
    public string Objetivo { get; set; } = "";
    public string Sql { get; set; } = "";
}

/// <summary>Textos do relatório gerencial padrão (Prompt 3). Parágrafos separados por linha em branco.</summary>
public class TextosRelatorio
{
    public string Introducao { get; set; } = "";
    public string AnaliseReceitas { get; set; } = "";
    public string AnaliseDespesas { get; set; } = "";
    public string AnaliseComparativa { get; set; } = "";
    public string Conclusao { get; set; } = "";
    public List<string> Sugestoes { get; set; } = [];

    // Um comentário analítico para CADA quadro (pedido da gerência: nenhum quadro sem comentário)
    public string ComentarioFontes { get; set; } = "";
    public string ComentarioOrgaosReceita { get; set; } = "";
    public string ComentarioReceitaMensal { get; set; } = "";
    public string ComentarioCategorias { get; set; } = "";
    public string ComentarioRubricas { get; set; } = "";
    public string ComentarioModalidade { get; set; } = "";
    public string ComentarioTiposDespesa { get; set; } = "";
    public string ComentarioDespesaMensal { get; set; } = "";
    public string ComentarioOrgaosDespesa { get; set; } = "";
    public string ComentarioFontesDespesa { get; set; } = "";
    public string ComentarioFuncoes { get; set; } = "";
    public string ComentarioSubfuncoes { get; set; } = "";
    public string ComentarioFuncoesDetalhadas { get; set; } = "";
    public string ComentarioComparativoFontes { get; set; } = "";
    public string ComentarioComparativoOrgaos { get; set; } = "";
    public string ComentarioComparativoFuncoes { get; set; } = "";
    public string ComentarioComparativoPagas { get; set; } = "";

    /// <summary>Nomes (camelCase) dos campos de texto — usados no schema enviado à IA.</summary>
    public static readonly string[] Campos =
    [
        "introducao", "analiseReceitas", "comentarioFontes", "comentarioOrgaosReceita", "comentarioReceitaMensal",
        "comentarioCategorias", "comentarioRubricas", "analiseDespesas", "comentarioModalidade", "comentarioTiposDespesa",
        "comentarioDespesaMensal", "comentarioOrgaosDespesa", "comentarioFontesDespesa", "comentarioFuncoes",
        "comentarioSubfuncoes", "comentarioFuncoesDetalhadas", "analiseComparativa", "comentarioComparativoFontes",
        "comentarioComparativoOrgaos", "comentarioComparativoFuncoes", "comentarioComparativoPagas", "conclusao"
    ];
}

/// <summary>Estrutura do relatório por consulta (Prompt 2).</summary>
public class EstruturaRelatorio
{
    public string Titulo { get; set; } = "";
    public string Subtitulo { get; set; } = "";
    public string Introducao { get; set; } = "";
    public List<SecaoRelatorio> Secoes { get; set; } = [];
    public string Conclusao { get; set; } = "";
    public List<string> Sugestoes { get; set; } = [];
}

public class SecaoRelatorio
{
    public string Titulo { get; set; } = "";
    public string TextoAntes { get; set; } = "";
    public TabelaPlanejada? Tabela { get; set; }
    /// <summary>Tabela transcrita de um arquivo anexado (valores exatamente como no arquivo).</summary>
    public TabelaAnexo? TabelaAnexo { get; set; }
    /// <summary>Imagem (foto/print) anexada pelo usuário, inserida no documento como está.</summary>
    public ImagemAnexo? ImagemAnexo { get; set; }
    public GraficoPlanejado? Grafico { get; set; }
    public string TextoDepois { get; set; } = "";
}

/// <summary>Imagem anexada a inserir na seção: nome do arquivo (como foi anexado) e a legenda.</summary>
public class ImagemAnexo
{
    public string Arquivo { get; set; } = "";
    public string Legenda { get; set; } = "";
}

/// <summary>Tabela copiada pela IA de um arquivo anexado (o banco não tem esses dados).</summary>
public class TabelaAnexo
{
    public string Titulo { get; set; } = "";
    public List<string> Cabecalho { get; set; } = [];
    public List<List<string>> Linhas { get; set; } = [];
    /// <summary>Nome do arquivo de origem (vai na nota "Fonte: ...").</summary>
    public string Fonte { get; set; } = "";
}

/// <summary>Gráfico pedido pela IA: o sistema desenha com os valores reais da consulta.</summary>
public class GraficoPlanejado
{
    public string ConsultaId { get; set; } = "";
    public string Titulo { get; set; } = "";
    /// <summary>colunas (comparar categorias/meses) | barras (ranking/parte do todo) | linha (evolução no tempo)</summary>
    public string Tipo { get; set; } = "colunas";
    public string CampoCategoria { get; set; } = "";
    public List<string> CamposValor { get; set; } = [];
    public List<string> NomesSeries { get; set; } = [];
    /// <summary>moeda | numero | percentual</summary>
    public string Formato { get; set; } = "moeda";
}

public class TabelaPlanejada
{
    public string ConsultaId { get; set; } = "";
    public string Titulo { get; set; } = "";
    public List<ColunaPlanejada> Colunas { get; set; } = [];
    public bool LinhaTotal { get; set; }
}

public class ColunaPlanejada
{
    public string Campo { get; set; } = "";
    public string Rotulo { get; set; } = "";
    /// <summary>texto | moeda | numero | percentual | participacao | variacao (campo × campoBase; sem base = linha anterior)</summary>
    public string Formato { get; set; } = "texto";
    public string? CampoBase { get; set; }
}

public static class JsonIA
{
    public static readonly JsonSerializerOptions Opcoes = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
