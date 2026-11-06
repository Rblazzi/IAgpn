using RelatorioIA.Api.Models;
using SkiaSharp;

namespace RelatorioIA.Api.Services.Documentos;

public enum TipoGrafico { Colunas, Barras, Linha, Rosca }

public record SerieGrafico(string Nome, double[] Valores);

/// <summary>Dados de um gráfico. Formato dos valores: moeda | numero | percentual.</summary>
public record GraficoDados(TipoGrafico Tipo, string[] Categorias, List<SerieGrafico> Series, string Formato = "moeda",
    string[]? RotulosFim = null);

/// <summary>
/// Desenha gráficos em PNG (SkiaSharp) para o Word e o PDF, com as especificações de visualização:
/// paleta categórica validada para daltonismo (ordem fixa, nunca reciclada), barras finas com ponta arredondada
/// de 4 px ancorada na linha de base, 2 px de espaço entre barras, linhas de 2 px com marcadores de 8 px,
/// grade e eixos discretos, legenda para 2+ séries e um único eixo Y. Parte do todo: rosca (pizza) com até 5 fatias;
/// acima disso, barras horizontais ordenadas com o percentual escrito.
/// </summary>
public static class GeradorGraficos
{
    // Paleta categórica (ordem fixa) — validada: CVD ΔE ≥ 9,1 entre vizinhas; contraste baixo das cores 3–4
    // é compensado pela legenda + tabela ao lado (relief rule).
    static readonly SKColor[] Serie = [Hex("#2a78d6"), Hex("#eb6834"), Hex("#1baf7a"), Hex("#eda100"), Hex("#e87ba4")];
    public const int MaxFatias = 5;   // rosca: 5 cores validadas (inclusive a vizinhança última↔primeira)
    static readonly SKColor Fundo = SKColors.White, TintaPrimaria = Hex("#0b0b0b"), TintaSecundaria = Hex("#52514e"),
        TintaSuave = Hex("#898781"), Grade = Hex("#e1e0d9"), Base = Hex("#c3c2b7");

    const float S = 2f;                 // escala (renderiza em 2x para impressão nítida)
    const float LarguraDp = 760f;       // ≈ 16 cm na página
    public const int MaxSeries = 4;

    static SKColor Hex(string h) => SKColor.Parse(h);
    static readonly SKTypeface Fonte = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;

    /// <summary>Gera o PNG e devolve a proporção altura/largura (para dimensionar no documento).</summary>
    public static (byte[] png, double proporcao) Desenhar(GraficoDados g)
    {
        if (g.Series.Count is 0 or > MaxSeries) throw new ArgumentException("O gráfico precisa de 1 a 4 séries.");
        if (g.Tipo == TipoGrafico.Rosca) return DesenharRosca(g);
        var alturaDp = g.Tipo == TipoGrafico.Barras
            ? 24 + (g.Series.Count > 1 ? 24 : 0) + g.Categorias.Length * 30f + 8
            : 300f;
        var w = (int)(LarguraDp * S); var h = (int)(alturaDp * S);

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(Fundo);
        c.Scale(S);

        var topo = 8f;
        if (g.Series.Count > 1) topo = Legenda(c, g.Series.Select(s => s.Nome).ToList(), 8, 8) + 12;

        if (g.Tipo == TipoGrafico.Barras) BarrasHorizontais(c, g, topo, LarguraDp, alturaDp);
        else ColunasOuLinha(c, g, topo, LarguraDp, alturaDp);

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return (data.ToArray(), alturaDp / LarguraDp);
    }

    // ------------------------------------------------------------------ colunas / linha (eixo Y à esquerda)

    static void ColunasOuLinha(SKCanvas c, GraficoDados g, float topo, float W, float H)
    {
        // NaN = ponto sem valor (ex.: meses futuros na série "realizado"): não entra na escala nem é desenhado
        var todos = g.Series.SelectMany(s => s.Valores).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).ToList();
        var (min, max, passo) = Escala(Math.Min(0, todos.Min()), Math.Max(0, todos.Max()));

        using var fonteEixo = new SKFont(Fonte, 10.5f);
        var rotulosY = new List<(double v, string t)>();
        for (var v = min; v <= max + passo / 2; v += passo) rotulosY.Add((v, Compacto(v, g.Formato)));
        var esquerda = 8 + rotulosY.Max(r => fonteEixo.MeasureText(r.t)) + 8;
        var direita = W - 12;
        var baixo = H - 30;
        float Y(double v) => (float)(baixo - (v - min) / (max - min) * (baixo - topo));

        using var pTexto = new SKPaint { Color = TintaSuave, IsAntialias = true };
        using var pGrade = new SKPaint { Color = Grade, StrokeWidth = 1f / S * 1.5f, IsAntialias = true };
        foreach (var (v, t) in rotulosY)
        {
            var y = Y(v);
            if (Math.Abs(v) > 1e-9) c.DrawLine(esquerda, y, direita, y, pGrade);
            c.DrawText(t, esquerda - 8, y + 3.5f, SKTextAlign.Right, fonteEixo, pTexto);
        }
        using var pBase = new SKPaint { Color = Base, StrokeWidth = 1f, IsAntialias = true };
        c.DrawLine(esquerda, Y(0), direita, Y(0), pBase);

        var n = g.Categorias.Length;
        var largGrupo = (direita - esquerda) / n;
        // rótulos do eixo X: pula alguns se não couberem
        var maxRotulo = g.Categorias.Max(x => fonteEixo.MeasureText(x));
        var pular = Math.Max(1, (int)Math.Ceiling((maxRotulo + 8) / largGrupo));
        using var pCat = new SKPaint { Color = TintaSecundaria, IsAntialias = true };
        for (int i = 0; i < n; i += pular)
            c.DrawText(Encurtar(g.Categorias[i], fonteEixo, largGrupo * pular - 4), esquerda + largGrupo * (i + 0.5f), baixo + 16,
                SKTextAlign.Center, fonteEixo, pCat);

        if (g.Tipo == TipoGrafico.Colunas)
        {
            var k = g.Series.Count;
            const float gap = 2f;
            var largBarra = Math.Min(28f, (largGrupo * 0.72f - gap * (k - 1)) / k);
            for (int i = 0; i < n; i++)
            {
                var x0 = esquerda + largGrupo * (i + 0.5f) - (largBarra * k + gap * (k - 1)) / 2;
                for (int s = 0; s < k; s++)
                {
                    var v = g.Series[s].Valores[i];
                    if (double.IsNaN(v)) continue;
                    var x = x0 + s * (largBarra + gap);
                    Barra(c, new SKRect(x, Math.Min(Y(v), Y(0)), x + largBarra, Math.Max(Y(v), Y(0))), Serie[s], v >= 0 ? Ponta.Cima : Ponta.Baixo);
                }
            }
        }
        else
        {
            for (int s = 0; s < g.Series.Count; s++)
            {
                using var pLinha = new SKPaint { Color = Serie[s], StrokeWidth = 2f, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };
                using var path = new SKPath();
                var continua = false;
                for (int i = 0; i < n; i++)
                {
                    var v = g.Series[s].Valores[i];
                    if (double.IsNaN(v)) { continua = false; continue; }   // interrompe a linha nos pontos sem valor
                    var p = new SKPoint(esquerda + largGrupo * (i + 0.5f), Y(v));
                    if (!continua) path.MoveTo(p); else path.LineTo(p);
                    continua = true;
                }
                c.DrawPath(path, pLinha);
                using var pAnel = new SKPaint { Color = Fundo, IsAntialias = true };
                using var pPonto = new SKPaint { Color = Serie[s], IsAntialias = true };
                if (n <= 24)
                    for (int i = 0; i < n; i++)
                    {
                        if (double.IsNaN(g.Series[s].Valores[i])) continue;
                        var p = new SKPoint(esquerda + largGrupo * (i + 0.5f), Y(g.Series[s].Valores[i]));
                        c.DrawCircle(p, 6f, pAnel);    // anel de 2 px da cor do fundo
                        c.DrawCircle(p, 4f, pPonto);   // marcador de 8 px
                    }
            }
        }
    }

    // ------------------------------------------------------------------ barras horizontais (ranking / parte do todo)

    static void BarrasHorizontais(SKCanvas c, GraficoDados g, float topo, float W, float H)
    {
        using var fonte = new SKFont(Fonte, 10.5f);
        using var pRotulo = new SKPaint { Color = TintaSecundaria, IsAntialias = true };
        using var pValor = new SKPaint { Color = TintaPrimaria, IsAntialias = true };

        var larguraRotulo = Math.Min(W * 0.36f, g.Categorias.Max(x => fonte.MeasureText(x)) + 12);
        var x0 = 8 + larguraRotulo;
        var fins = g.RotulosFim ?? g.Series[0].Valores.Select(v => Completo(v, g.Formato)).ToArray();
        var larguraFim = fins.Max(t => fonte.MeasureText(t)) + 10;
        static double Z(double v) => double.IsNaN(v) ? 0 : v;   // ponto sem valor = barra vazia
        g = g with { Series = g.Series.Select(s => s with { Valores = s.Valores.Select(Z).ToArray() }).ToList() };
        var max = Math.Max(1e-9, g.Series.SelectMany(s => s.Valores).Select(Math.Abs).Max());
        var larguraUtil = W - x0 - larguraFim - 8;

        using var pBase = new SKPaint { Color = Base, StrokeWidth = 1f, IsAntialias = true };
        var linha = 30f;
        var k = g.Series.Count;
        var espessura = Math.Min(16f, (linha - 10 - 2 * (k - 1)) / k);
        for (int i = 0; i < g.Categorias.Length; i++)
        {
            var yLinha = topo + i * linha;
            var meio = yLinha + linha / 2;
            c.DrawText(Encurtar(g.Categorias[i], fonte, larguraRotulo - 12), 8, meio + 3.5f, SKTextAlign.Left, fonte, pRotulo);
            var alturaGrupo = espessura * k + 2 * (k - 1);
            for (int s = 0; s < k; s++)
            {
                var v = Math.Max(0, g.Series[s].Valores[i]);
                var y = meio - alturaGrupo / 2 + s * (espessura + 2);
                var comp = (float)(v / max * larguraUtil);
                if (comp > 0.5f) Barra(c, new SKRect(x0, y, x0 + comp, y + espessura), Serie[s], Ponta.Direita);
            }
            if (k == 1)
            {
                var comp = (float)(Math.Max(0, g.Series[0].Valores[i]) / max * larguraUtil);
                c.DrawText(fins[i], x0 + comp + 6, meio + 3.5f, SKTextAlign.Left, fonte, pValor);
            }
        }
        c.DrawLine(x0, topo, x0, topo + g.Categorias.Length * linha, pBase);
    }

    // ------------------------------------------------------------------ rosca (padrão visual do relatório da consultoria)

    /// <summary>
    /// Rosca com até 5 fatias (maior primeiro, a partir das 12h, sentido horário), 2 px de separação branca entre
    /// fatias, total no centro e legenda ao lado com nome + "valor · %" de cada fatia (identidade nunca só pela cor).
    /// </summary>
    static (byte[] png, double proporcao) DesenharRosca(GraficoDados g)
    {
        var itens = g.Categorias.Zip(g.Series[0].Valores, (n, v) => (nome: n, valor: Math.Max(0, v)))
            .Select((x, i) => (x.nome, x.valor, rotulo: g.RotulosFim?.ElementAtOrDefault(i)))
            .Where(x => x.valor > 0).Take(MaxFatias).ToList();
        var total = itens.Sum(x => x.valor);
        if (itens.Count == 0 || total <= 0) throw new ArgumentException("Rosca sem valores positivos.");

        const float raio = 92f, raioInterno = raio * 0.58f, margem = 16f;
        var alturaDp = Math.Max(raio * 2 + margem * 2, 16 + itens.Count * 40f + 16);
        var w = (int)(LarguraDp * S); var h = (int)(alturaDp * S);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(Fundo);
        c.Scale(S);

        var centro = new SKPoint(margem + raio + 24, alturaDp / 2);
        var externo = new SKRect(centro.X - raio, centro.Y - raio, centro.X + raio, centro.Y + raio);
        var interno = new SKRect(centro.X - raioInterno, centro.Y - raioInterno, centro.X + raioInterno, centro.Y + raioInterno);

        float inicio = -90f;
        using var separador = new SKPaint { Color = Fundo, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
        for (int i = 0; i < itens.Count; i++)
        {
            var varredura = (float)(itens[i].valor / total * 360.0);
            using var path = new SKPath();
            path.ArcTo(externo, inicio, varredura, true);
            path.ArcTo(interno, inicio + varredura, -varredura, false);
            path.Close();
            using var p = new SKPaint { Color = Serie[i], IsAntialias = true };
            c.DrawPath(path, p);
            if (itens.Count > 1) c.DrawPath(path, separador);   // 2 px de espaço entre fatias
            inicio += varredura;
        }

        // Total no centro
        using var fonteRotulo = new SKFont(Fonte, 10f);
        using var fonteTotal = new SKFont(Fonte, 13f) { Embolden = true };
        using var pSuave = new SKPaint { Color = TintaSuave, IsAntialias = true };
        using var pPrim = new SKPaint { Color = TintaPrimaria, IsAntialias = true };
        c.DrawText("Total", centro.X, centro.Y - 4, SKTextAlign.Center, fonteRotulo, pSuave);
        c.DrawText(Compacto(total, g.Formato), centro.X, centro.Y + 13, SKTextAlign.Center, fonteTotal, pPrim);

        // Legenda: nome (tinta secundária) + valor · % (tinta primária); a cor fica só no marcador
        using var fonteNome = new SKFont(Fonte, 10.5f);
        using var fonteValor = new SKFont(Fonte, 11f) { Embolden = true };
        using var pSec = new SKPaint { Color = TintaSecundaria, IsAntialias = true };
        var xLeg = centro.X + raio + 36;
        var larguraLeg = LarguraDp - xLeg - 12;
        var yLeg = alturaDp / 2 - itens.Count * 40f / 2;
        for (int i = 0; i < itens.Count; i++)
        {
            var y = yLeg + i * 40f;
            using var p = new SKPaint { Color = Serie[i], IsAntialias = true };
            c.DrawRoundRect(new SKRoundRect(new SKRect(xLeg, y + 6, xLeg + 12, y + 18), 3), p);
            c.DrawText(Encurtar(itens[i].nome, fonteNome, larguraLeg - 20), xLeg + 20, y + 15, SKTextAlign.Left, fonteNome, pSec);
            var rot = itens[i].rotulo ?? $"{Compacto(itens[i].valor, g.Formato)} · {Fmt.Pct((decimal)(itens[i].valor / total * 100), 1)}";
            c.DrawText(rot, xLeg + 20, y + 31, SKTextAlign.Left, fonteValor, pPrim);
        }

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return (data.ToArray(), alturaDp / LarguraDp);
    }

    // ------------------------------------------------------------------ peças

    enum Ponta { Cima, Baixo, Direita }

    /// <summary>Barra com ponta de dados arredondada (4 px) e a base reta, ancorada na linha de base.</summary>
    static void Barra(SKCanvas c, SKRect r, SKColor cor, Ponta ponta)
    {
        if (r.Height < 0.5f || r.Width < 0.5f) return;
        var raio = Math.Min(4f, Math.Min(r.Width, r.Height) / 2);
        var z = new SKPoint(0, 0); var a = new SKPoint(raio, raio);
        var raios = ponta switch
        {
            Ponta.Cima => new[] { a, a, z, z },      // sup-esq, sup-dir, inf-dir, inf-esq
            Ponta.Baixo => new[] { z, z, a, a },
            _ => new[] { z, a, a, z }
        };
        using var rr = new SKRoundRect();
        rr.SetRectRadii(r, raios);
        using var p = new SKPaint { Color = cor, IsAntialias = true };
        c.DrawRoundRect(rr, p);
    }

    static float Legenda(SKCanvas c, List<string> nomes, float x, float y)
    {
        using var fonte = new SKFont(Fonte, 11f);
        using var pTexto = new SKPaint { Color = TintaSecundaria, IsAntialias = true };
        for (int i = 0; i < nomes.Count; i++)
        {
            using var p = new SKPaint { Color = Serie[i], IsAntialias = true };
            c.DrawRoundRect(new SKRoundRect(new SKRect(x, y + 1, x + 10, y + 11), 2), p);
            c.DrawText(nomes[i], x + 15, y + 10, SKTextAlign.Left, fonte, pTexto);
            x += 15 + fonte.MeasureText(nomes[i]) + 18;
        }
        return y + 12;
    }

    static string Encurtar(string t, SKFont f, float max)
    {
        if (f.MeasureText(t) <= max) return t;
        while (t.Length > 1 && f.MeasureText(t + "…") > max) t = t[..^1];
        return t.TrimEnd() + "…";
    }

    /// <summary>Escala "bonita": limites e passo arredondados (1, 2, 2,5, 5 × 10^n), ~5 divisões.</summary>
    static (double min, double max, double passo) Escala(double min, double max)
    {
        if (Math.Abs(max - min) < 1e-9) max = min + 1;
        var bruto = (max - min) / 5;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(bruto)));
        var passo = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * mag).First(p => p >= bruto);
        return (Math.Floor(min / passo) * passo, Math.Ceiling(max / passo) * passo, passo);
    }

    /// <summary>Rótulo de eixo compacto: "R$ 12,5 mi", "R$ 800 mil", "35%".</summary>
    public static string Compacto(double v, string formato)
    {
        if (formato == "percentual") return v.ToString("0.#", Fmt.PtBr) + "%";
        var a = Math.Abs(v);
        var t = a >= 1e9 ? (v / 1e9).ToString("0.#", Fmt.PtBr) + " bi"
              : a >= 1e6 ? (v / 1e6).ToString("0.#", Fmt.PtBr) + " mi"
              : a >= 1e3 ? (v / 1e3).ToString("0.#", Fmt.PtBr) + " mil"
              : v.ToString("0.#", Fmt.PtBr);
        return formato == "moeda" ? "R$ " + t : t;
    }

    static string Completo(double v, string formato) => formato switch
    {
        "moeda" => Compacto(v, "moeda"),
        "percentual" => Fmt.Pct((decimal)v),
        _ => v.ToString("N0", Fmt.PtBr)
    };
}
