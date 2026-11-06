using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RelatorioIA.Api.Services.IA;

public class GeminiOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.1-flash-lite"; // mesmo modelo do outro sistema da empresa
    /// <summary>Modelos tentados em ordem quando o principal está sobrecarregado (503/429/500).</summary>
    public string[] ModelosReserva { get; set; } =
        ["gemini-3.5-flash-lite", "gemini-flash-lite-latest", "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3.8-flash"];
    /// <summary>Em horários de pico o Gemini leva 15–25 s por chamada; 60 s evita cair no modo local à toa.</summary>
    public int TimeoutSegundos { get; set; } = 60;
    /// <summary>
    /// Se o modelo principal não responder em N segundos, dispara uma 2ª chamada em paralelo e usa a que chegar
    /// primeiro (as respostas normais levam de 1 a 8 s, até ~20 s em pico; às vezes uma chamada "trava" até o
    /// timeout). 0 = desliga.
    /// </summary>
    public int SegundaChamadaAposSegundos { get; set; } = 25;
    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
}

/// <summary>Gemini via REST (generateContent) com saída JSON estruturada (responseSchema), retentativa e modelos reserva.</summary>
public class GeminiModelo(HttpClient http, IOptions<GeminiOptions> opt, ILogger<GeminiModelo> log) : IModeloIA
{
    readonly GeminiOptions o = opt.Value;
    public string Nome => $"Gemini ({o.Model})";

    public async Task<JsonElement> GerarJsonAsync(string sistema, IEnumerable<(string papel, string texto)> conversa,
        object schema, double temperatura, CancellationToken ct, IReadOnlyList<AnexoConteudo>? anexos = null)
    {
        var mensagens = conversa.ToList();
        var contents = mensagens.Select((c, i) =>
        {
            var parts = new List<object> { new { text = c.texto } };
            // Anexos vão na última mensagem do usuário: texto extraído (Word/Excel/CSV) ou o arquivo (PDF/imagem)
            if (i == mensagens.Count - 1 && anexos is { Count: > 0 })
                foreach (var a in anexos)
                {
                    parts.Add(new { text = $"\n=== ARQUIVO ANEXADO PELO USUÁRIO: \"{a.Nome}\" ===" + (a.Texto is null ? "" : "\n" + a.Texto) });
                    if (a.Dados is not null)
                        parts.Add(new { inlineData = new { mimeType = a.Mime, data = Convert.ToBase64String(a.Dados) } });
                }
            return new { role = c.papel == "assistant" ? "model" : "user", parts };
        }).ToList();
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = sistema } } },
            contents,
            generationConfig = new { responseMimeType = "application/json", responseSchema = schema, temperature = temperatura }
        };

        var modelos = new[] { o.Model }.Concat(o.ModelosReserva).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
        Exception? ultimo = null;
        foreach (var modelo in modelos)
        {
            for (int tentativa = 1; tentativa <= (modelo == o.Model ? 2 : 1); tentativa++)
            {
                try
                {
                    var (status, raw) = modelo == o.Model && tentativa == 1 && o.SegundaChamadaAposSegundos > 0
                        ? await ChamarComSegundaAsync(modelo, body, ct)
                        : await ChamarAsync(modelo, body, ct);
                    if (status is >= 200 and < 300)
                    {
                        if (modelo != o.Model) log.LogInformation("Gemini: respondido pelo modelo reserva {Modelo}", modelo);
                        RegistrarUso(raw, modelo);
                        return ExtrairJson(raw);
                    }
                    ultimo = new HttpRequestException($"Gemini HTTP {status} ({modelo}): {raw[..Math.Min(raw.Length, 300)]}");
                    if (status is not (429 or 500 or 503 or 504)) throw ultimo;
                    log.LogWarning("Gemini {Modelo} indisponível (HTTP {Status}), tentativa {T}", modelo, status, tentativa);
                }
                catch (TimeoutException ex)
                {
                    ultimo = ex;
                    log.LogWarning("Gemini {Modelo} timeout, tentativa {T}", modelo, tentativa);
                }
                await Task.Delay(TimeSpan.FromSeconds(1.5 * tentativa), ct);
            }
        }
        throw ultimo ?? new HttpRequestException("Gemini indisponível");
    }

    /// <summary>Uma chamada com timeout próprio. Devolve o status HTTP e o corpo; timeout → TimeoutException.</summary>
    async Task<(int status, string raw)> ChamarAsync(string modelo, object body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSegundos));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{o.BaseUrl}/models/{modelo}:generateContent")
            {
                Content = JsonContent.Create(body)
            };
            req.Headers.Add("x-goog-api-key", o.ApiKey);
            using var resp = await http.SendAsync(req, cts.Token);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Gemini ({modelo}) não respondeu em {o.TimeoutSegundos}s");
        }
    }

    /// <summary>
    /// Chamada "com reserva": se a primeira demora mais que o normal, dispara uma segunda igual em paralelo e fica
    /// com a primeira resposta válida (a outra é cancelada). Evita esperar o timeout inteiro quando uma chamada trava.
    /// </summary>
    async Task<(int status, string raw)> ChamarComSegundaAsync(string modelo, object body, CancellationToken ct)
    {
        using var corrida = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var a = ChamarAsync(modelo, body, corrida.Token);
        if (await Task.WhenAny(a, Task.Delay(TimeSpan.FromSeconds(o.SegundaChamadaAposSegundos), ct)) == a)
            return await a;

        log.LogInformation("Gemini {Modelo} demorando mais de {S}s; disparando uma segunda chamada em paralelo", modelo, o.SegundaChamadaAposSegundos);
        var b = ChamarAsync(modelo, body, corrida.Token);
        var primeira = await Task.WhenAny(a, b);
        var outra = primeira == a ? b : a;
        _ = outra.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);   // perdedora: erro ignorado
        try
        {
            try
            {
                var r = await primeira;
                if (r.status is >= 200 and < 300) return r;
            }
            catch (TimeoutException) { }
            catch (HttpRequestException) { }
            return await outra;
        }
        finally { corrida.Cancel(); }   // cancela a chamada que ainda estiver em andamento
    }

    /// <summary>Tokens consumidos (para acompanhar o custo): entrada, saída e "pensamento" (cobrado como saída).</summary>
    void RegistrarUso(string raw, string modelo)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("usageMetadata", out var u)) return;
            int N(string p) => u.TryGetProperty(p, out var v) ? v.GetInt32() : 0;
            log.LogInformation("Gemini uso {Modelo}: entrada={Entrada} saida={Saida} pensamento={Pensamento} tokens",
                modelo, N("promptTokenCount"), N("candidatesTokenCount"), N("thoughtsTokenCount"));
        }
        catch (JsonException) { }
    }

    static JsonElement ExtrairJson(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var texto = doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0]
            .GetProperty("text").GetString() ?? "{}";
        using var saida = JsonDocument.Parse(texto);
        return saida.RootElement.Clone();
    }
}
