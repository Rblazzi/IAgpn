namespace RelatorioIA.Api.Models;

/// <summary>
/// Documento padrão do relatório (independente de formato). Tanto o relatório gerencial quanto os relatórios
/// montados a partir de consultas SQL da IA viram este modelo; depois ele é renderizado em Word e PDF
/// com o mesmo visual (capa, faixa rosa, títulos azuis, tabelas com bordas, encerramento).
/// </summary>
[System.Text.Json.Serialization.JsonObjectCreationHandling(System.Text.Json.Serialization.JsonObjectCreationHandling.Populate)]
public class Documento
{
    /// <summary>Troca a linha de assinatura (último parágrafo, escrito pelo encerramento padrão).</summary>
    public void TrocarAssinatura(string quem)
    {
        var i = Blocos.FindLastIndex(b => b is Paragrafo);
        if (i >= 0) Blocos[i] = new Paragrafo(quem.ToUpper(new System.Globalization.CultureInfo("pt-BR")), true, false, Alinhamento.Centro, 11);
    }

    public required string Titulo { get; init; }            // "RELATÓRIO GERENCIAL"
    public required string Municipio { get; init; }         // destaque da capa
    public string Subtitulo { get; init; } = "Análise financeira sobre as contas públicas municipais";
    public string PeriodoAnalisado { get; init; } = "";     // "Janeiro a Maio de 2026"
    public bool Sumario { get; init; } = true;
    /// <summary>Brasão(ões) do(s) município(s) em PNG, no topo da capa (comparativo entre cidades: um ao lado do outro).</summary>
    public List<byte[]> Brasoes { get; } = [];
    public List<Bloco> Blocos { get; } = [];

    public Documento T1(string t) { Blocos.Add(new Titulo(1, t)); return this; }
    public Documento T2(string t) { Blocos.Add(new Titulo(2, t)); return this; }
    public Documento T3(string t) { Blocos.Add(new Titulo(3, t)); return this; }

    /// <param name="manterComProximo">Não separa este parágrafo do seguinte na quebra de página (bloco de assinatura).</param>
    public Documento P(string t, bool negrito = false, bool italico = false, Alinhamento alin = Alinhamento.Justificado, int tamanho = 11,
        bool manterComProximo = false)
    {
        if (!string.IsNullOrWhiteSpace(t) || alin != Alinhamento.Justificado)
            Blocos.Add(new Paragrafo(t, negrito, italico, alin, tamanho, manterComProximo));
        return this;
    }

    /// <summary>Texto com vários parágrafos separados por linha em branco.</summary>
    public Documento Textos(string? texto)
    {
        foreach (var par in (texto ?? "").Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries))
            P(par.Replace("\n", " ").Trim());
        return this;
    }

    public Documento Tab(TabelaDoc t) { Blocos.Add(t); return this; }
}

public enum Alinhamento { Justificado, Esquerda, Centro, Direita }

// Tipos de bloco para salvar/ler o documento em JSON (permite reabrir o relatório e alterar, ex.: a assinatura)
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "tipo")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Titulo), "titulo")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(Paragrafo), "paragrafo")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(QuebraPagina), "quebra")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(GraficoDoc), "grafico")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(TabelaDoc), "tabela")]
public abstract record Bloco;
public record Titulo(int Nivel, string Texto) : Bloco;
public record Paragrafo(string Texto, bool Negrito, bool Italico, Alinhamento Alinhamento, int Tamanho, bool ManterComProximo = false) : Bloco;
public record QuebraPagina : Bloco;

/// <summary>Gráfico já desenhado (PNG). Proporcao = altura / largura; a largura ocupa a área útil da página.</summary>
public record GraficoDoc(string Titulo, byte[] Png, double Proporcao, string? Nota = null) : Bloco;

/// <summary>Tabela já formatada (texto). Larguras em % (somam 100).</summary>
public record TabelaDoc : Bloco
{
    public string? Titulo { get; init; }
    public string[]? Cabecalho { get; init; }
    public required List<string[]> Linhas { get; init; }
    public required int[] Larguras { get; init; }
    public int NegritoUltimas { get; init; }
    public ISet<int>? LinhasNegrito { get; init; }
    public bool CentralizarPrimeira { get; init; }
    public double Fonte { get; init; } = 8.5;
    public string? Nota { get; init; }

    public bool Negrito(int i) => i >= Linhas.Count - NegritoUltimas || (LinhasNegrito?.Contains(i) ?? false);
}
