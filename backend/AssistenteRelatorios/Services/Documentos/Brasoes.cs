using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Models;
using SkiaSharp;

namespace RelatorioIA.Api.Services.Documentos;

/// <summary>
/// Fonte do brasão do município para a capa do relatório. Hoje: Wikidata/Wikimedia Commons (BrasaoWikidata).
/// No futuro, uma implementação que lê do banco do painel só precisa trocar o registro no AssistenteRelatoriosSetup.
/// </summary>
public interface IFonteBrasao
{
    /// <summary>PNG do brasão (nome de exibição do município), ou null se não houver/não for possível obter.</summary>
    Task<byte[]?> ObterAsync(string municipio, CancellationToken ct = default);
}

/// <summary>
/// Brasões via Wikidata (propriedade P94 "brasão de armas" dos municípios, ligados pelo código IBGE P1585).
/// 1ª vez: baixa o índice dos municípios da UF (1 consulta SPARQL) e a imagem do Commons; tudo fica em cache
/// em disco (Relatorio:PastaBrasoes, padrão "brasoes"): depois disso, nada vai à internet. Para corrigir um
/// brasão, basta substituir o arquivo {codigo}.png na pasta.
/// </summary>
public class BrasaoWikidata(HttpClient http, IMemoryCache cache, IOptions<RelatorioOptions> opt, IWebHostEnvironment env,
    IConfiguration cfg, ILogger<BrasaoWikidata> log) : IFonteBrasao
{
    static readonly SemaphoreSlim trava = new(1, 1);
    const int LadoMaximo = 400;
    string Pasta => Path.GetFullPath(Path.Combine(env.ContentRootPath, opt.Value.PastaBrasoes));
    string Uf => cfg["Relatorio:UfIbge"] ?? "35";   // 35 = São Paulo (TCESP)

    public async Task<byte[]?> ObterAsync(string municipio, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(municipio)) return null;
        var chave = Normalizar(municipio);
        var arquivo = Path.Combine(Pasta, Regex.Replace(chave, " ", "-") + ".png");
        if (File.Exists(arquivo)) return await File.ReadAllBytesAsync(arquivo, ct);
        if (cache.TryGetValue("brasao-falta:" + chave, out _)) return null;   // falhou há pouco: não tenta de novo

        await trava.WaitAsync(ct);
        try
        {
            if (File.Exists(arquivo)) return await File.ReadAllBytesAsync(arquivo, ct);
            var indice = await IndiceAsync(ct);
            if (!indice.TryGetValue(chave, out var url))
            {
                log.LogInformation("Brasão de {Municipio} não cadastrado no Wikidata", municipio);
                cache.Set("brasao-falta:" + chave, true, TimeSpan.FromHours(12));
                return null;
            }
            var png = ParaPng(await http.GetByteArrayAsync($"{url}?width={LadoMaximo}", ct));
            Directory.CreateDirectory(Pasta);
            await File.WriteAllBytesAsync(arquivo, png, ct);
            log.LogInformation("Brasão de {Municipio} baixado do Wikimedia Commons: {Url}", municipio, url);
            return png;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Não foi possível obter o brasão de {Municipio}; a capa sai sem brasão", municipio);
            cache.Set("brasao-falta:" + chave, true, TimeSpan.FromMinutes(30));
            return null;
        }
        finally { trava.Release(); }
    }

    /// <summary>Nome normalizado → URL da imagem no Commons, para todos os municípios da UF. Renovado a cada 30 dias.</summary>
    async Task<Dictionary<string, string>> IndiceAsync(CancellationToken ct)
    {
        var arq = Path.Combine(Pasta, $"indice-wikidata-{Uf}.json");
        if (File.Exists(arq) && File.GetLastWriteTimeUtc(arq) > DateTime.UtcNow.AddDays(-30))
            return JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(arq, ct)) ?? [];

        var sparql = $$"""
            SELECT ?nome (SAMPLE(?img) AS ?brasao) WHERE {
              ?m wdt:P1585 ?ibge; wdt:P94 ?img; rdfs:label ?nome.
              FILTER(STRSTARTS(?ibge, "{{Uf}}") && STRLEN(?ibge) = 7 && LANG(?nome) = "pt")
            } GROUP BY ?nome
            """;
        var resp = await http.GetStringAsync("https://query.wikidata.org/sparql?format=json&query=" + Uri.EscapeDataString(sparql), ct);
        using var json = JsonDocument.Parse(resp);
        var indice = new Dictionary<string, string>();
        foreach (var b in json.RootElement.GetProperty("results").GetProperty("bindings").EnumerateArray())
            indice.TryAdd(Normalizar(b.GetProperty("nome").GetProperty("value").GetString()!),
                b.GetProperty("brasao").GetProperty("value").GetString()!.Replace("http://", "https://"));

        Directory.CreateDirectory(Pasta);
        await File.WriteAllTextAsync(arq, JsonSerializer.Serialize(indice), ct);
        log.LogInformation("Índice de brasões do Wikidata atualizado: {N} municípios (UF {Uf})", indice.Count, Uf);
        return indice;
    }

    /// <summary>Converte JPG/PNG/GIF (SVG já vem rasterizado pelo Commons) em PNG de no máximo 400 px.</summary>
    static byte[] ParaPng(byte[] dados)
    {
        using var bmp = SKBitmap.Decode(dados) ?? throw new InvalidDataException("imagem do brasão inválida");
        var escala = Math.Min(1.0, (double)LadoMaximo / Math.Max(bmp.Width, bmp.Height));
        using var final = escala < 1
            ? bmp.Resize(new SKImageInfo((int)(bmp.Width * escala), (int)(bmp.Height * escala)), new SKSamplingOptions(SKCubicResampler.Mitchell))
            : bmp.Copy();
        using var img = SKImage.FromBitmap(final);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>"Santa Bárbara d'Oeste" → "santa barbara d oeste" (sem acento, minúsculas, só letras/números).</summary>
    public static string Normalizar(string s)
    {
        var sem = new string(s.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Replace(sem.ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
    }
}
