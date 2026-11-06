using System.Text.Json;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Services.Documentos;

namespace RelatorioIA.Api.Services.IA;

/// <summary>
/// Nomes dos municípios da UF (SP), para o modo sem IA reconhecer quando o usuário cita uma cidade que NÃO está no
/// banco. Usa o índice do Wikidata já baixado para os brasões (brasoes/indice-wikidata-35.json); se ainda não existir,
/// a lista fica vazia e nada muda.
/// </summary>
public class MunicipiosConhecidos(IWebHostEnvironment env, IOptions<RelatorioOptions> opt, IConfiguration cfg)
{
    IReadOnlyList<string>? nomes;
    DateTime lidoEm;

    /// <summary>Nomes normalizados (sem acento, minúsculas), ex.: "sao joao da boa vista".</summary>
    public IReadOnlyList<string> Nomes()
    {
        if (nomes is { Count: > 0 } && lidoEm > DateTime.UtcNow.AddHours(-1)) return nomes;
        var arq = Path.Combine(env.ContentRootPath, opt.Value.PastaBrasoes, $"indice-wikidata-{cfg["Relatorio:UfIbge"] ?? "35"}.json");
        try
        {
            nomes = File.Exists(arq)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(arq))?.Keys.ToList() ?? []
                : [];
        }
        catch (Exception) { nomes = []; }
        lidoEm = DateTime.UtcNow;
        return nomes;
    }

    // Nomes de município que também são palavras comuns em pedidos financeiros: não contam sozinhos
    static readonly HashSet<string> Ambiguos = ["planalto", "pontal", "bastos", "oleo", "paraiso", "itu", "lins", "ouro verde", "saude"];

    /// <summary>Municípios citados no texto que não estão no banco, como o usuário escreveu (ex.: "São João da Boa Vista").</summary>
    public static List<string> CitadosForaDoBanco(string texto, IEnumerable<string> conhecidos, IEnumerable<string> doBanco)
    {
        var palavras = System.Text.RegularExpressions.Regex.Matches(texto, @"[\p{L}\p{N}]+")
            .Select(m => (m.Index, Fim: m.Index + m.Length, Norm: BrasaoWikidata.Normalizar(m.Value))).ToList();
        var normas = palavras.Select(p => p.Norm).ToList();
        var banco = doBanco.Select(BrasaoWikidata.Normalizar).ToHashSet();

        var achados = new List<(string norm, string original)>();
        foreach (var n in conhecidos)
        {
            if (banco.Contains(n) || Ambiguos.Contains(n) || (!n.Contains(' ') && n.Length < 8)) continue;
            var tokens = n.Split(' ');
            for (int i = 0; i + tokens.Length <= normas.Count; i++)
                if (normas.Skip(i).Take(tokens.Length).SequenceEqual(tokens))
                {
                    achados.Add((n, texto[palavras[i].Index..palavras[i + tokens.Length - 1].Fim]));
                    break;
                }
        }
        // "sao joao da boa vista" contém "boa vista": fica só o nome mais longo
        return achados.Where(a => !achados.Any(b => b.norm != a.norm && $" {b.norm} ".Contains($" {a.norm} ")))
            .Select(a => a.original).ToList();
    }
}
