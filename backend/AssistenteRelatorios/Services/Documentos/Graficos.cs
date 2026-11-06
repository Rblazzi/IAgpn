using RelatorioIA.Api.Models;

namespace RelatorioIA.Api.Services.Documentos;

public static class Graficos
{
    /// <summary>
    /// Desenha e adiciona um gráfico ao documento. Falha ao desenhar NUNCA derruba o relatório:
    /// o gráfico é apenas omitido (a tabela com os mesmos números continua lá).
    /// </summary>
    public static Documento Grafico(this Documento doc, string titulo, GraficoDados dados, string? nota = null, ILogger? log = null)
    {
        try
        {
            if (dados.Categorias.Length == 0 || dados.Series.Count == 0) return doc;
            if (dados.Series.Any(s => s.Valores.Length != dados.Categorias.Length)) return doc;
            if (dados.Series.All(s => s.Valores.All(v => Math.Abs(v) < 0.005))) return doc;
            var (png, proporcao) = GeradorGraficos.Desenhar(dados);
            doc.Blocos.Add(new GraficoDoc(titulo, png, proporcao, nota));
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Gráfico \"{Titulo}\" não pôde ser desenhado e foi omitido", titulo);
        }
        return doc;
    }

    /// <summary>Rosca (padrão do relatório da consultoria): até 5 fatias, excedente agrupado em "DEMAIS".</summary>
    public static GraficoDados Rosca(IEnumerable<(string rotulo, decimal valor)> itens, decimal total, string rotuloDemais = "DEMAIS")
    {
        var lista = itens.Where(x => x.valor > 0).OrderByDescending(x => x.valor).ToList();
        if (lista.Count > GeradorGraficos.MaxFatias)
        {
            var resto = lista.Skip(GeradorGraficos.MaxFatias - 1).Sum(x => x.valor);
            lista = lista.Take(GeradorGraficos.MaxFatias - 1).Append((rotuloDemais, resto)).ToList();
        }
        return new GraficoDados(TipoGrafico.Rosca,
            lista.Select(x => x.rotulo).ToArray(),
            [new SerieGrafico("Valor", lista.Select(x => (double)x.valor).ToArray())],
            // Valor completo (como no relatório da consultoria) + participação
            RotulosFim: lista.Select(x => $"{Fmt.Moeda(x.valor)} · {Fmt.Pct(total == 0 ? null : x.valor / total * 100m, 2)}").ToArray());
    }

    /// <summary>Ranking horizontal com "valor · %" no fim de cada barra; agrupa o excedente em "DEMAIS".</summary>
    public static GraficoDados Ranking(IEnumerable<(string rotulo, decimal valor)> itens, decimal total, int max = 8, string rotuloDemais = "DEMAIS")
    {
        var lista = itens.Where(x => x.valor > 0).OrderByDescending(x => x.valor).ToList();
        if (lista.Count > max)
        {
            var resto = lista.Skip(max - 1).Sum(x => x.valor);
            lista = lista.Take(max - 1).Append((rotuloDemais, resto)).ToList();
        }
        return new GraficoDados(TipoGrafico.Barras,
            lista.Select(x => x.rotulo).ToArray(),
            [new SerieGrafico("Valor", lista.Select(x => (double)x.valor).ToArray())],
            RotulosFim: lista.Select(x => $"{GeradorGraficos.Compacto((double)x.valor, "moeda")} · {Fmt.Pct(total == 0 ? null : x.valor / total * 100m, 1)}").ToArray());
    }
}
