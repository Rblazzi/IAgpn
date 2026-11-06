using RelatorioIA.Api.Models;
using SkiaSharp;

namespace RelatorioIA.Api.Services.Documentos;

/// <summary>
/// Capa do relatório desenhada como uma página A4 inteira (SkiaSharp), usada igual no Word e no PDF:
/// faixa azul-marinho com o título, brasão(ões) em destaque, nome do município, quadro de informações
/// (período, fonte, emissão) e rodapé com a consultoria. Medidas em milímetros; renderizada a 200 dpi.
/// </summary>
public static class CapaDesenho
{
    public const double LarguraMm = 210, AlturaMm = 297;
    const float Dpi = 200f;
    const float PxMm = Dpi / 25.4f;           // pixels por milímetro
    const float PxPt = Dpi / 72f;             // pixels por ponto tipográfico

    static readonly SKColor Marinho = SKColor.Parse("#2E3192"), MarinhoEscuro = SKColor.Parse("#1B1D5E"),
        Rosa = SKColor.Parse("#E8407A"), AzulClaro = SKColor.Parse("#BFD9FB"), Texto = SKColor.Parse("#2B2D42"),
        Cinza = SKColor.Parse("#6B6E82"), Linha = SKColor.Parse("#E2E4EE"), Fundo = SKColors.White;

    static SKTypeface Fonte(bool negrito) =>
        SKTypeface.FromFamilyName("Segoe UI", negrito ? SKFontStyle.Bold : SKFontStyle.Normal)
        ?? SKTypeface.FromFamilyName("Arial", negrito ? SKFontStyle.Bold : SKFontStyle.Normal)
        ?? SKTypeface.Default;

    public static byte[] Desenhar(Documento d, RelatorioOptions o, DateTime emissao)
    {
        var w = (int)Math.Round(LarguraMm * PxMm); var h = (int)Math.Round(AlturaMm * PxMm);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(Fundo);
        c.Scale(PxMm);   // a partir daqui, tudo em milímetros

        var temBrasao = d.Brasoes.Count > 0;
        const float alturaFaixa = 118f;

        // ---------- faixa superior (gradiente marinho + formas geométricas discretas)
        using (var p = new SKPaint { IsAntialias = true })
        {
            p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint((float)LarguraMm, alturaFaixa),
                [Marinho, MarinhoEscuro], SKShaderTileMode.Clamp);
            c.DrawRect(0, 0, (float)LarguraMm, alturaFaixa, p);
        }
        c.Save();
        c.ClipRect(new SKRect(0, 0, (float)LarguraMm, alturaFaixa));
        using (var p = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(13) })
        {
            c.DrawCircle(228, -12, 76, p);
            c.DrawCircle(222, 112, 46, p);
        }
        using (var p = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(22), Style = SKPaintStyle.Stroke, StrokeWidth = 0.35f })
        {
            c.DrawCircle(228, -12, 90, p);
            c.DrawCircle(222, 112, 58, p);
        }
        c.Restore();
        using (var p = new SKPaint { Color = Rosa }) c.DrawRect(0, alturaFaixa, (float)LarguraMm, 1.6f, p);

        // ---------- textos da faixa
        const float margem = 22f, larguraTexto = (float)LarguraMm - 2 * margem;
        using (var p = new SKPaint { Color = Rosa }) c.DrawRect(margem, 24, 14, 1.2f, p);
        Escrever(c, o.Consultoria.ToUpper(Fmt.PtBr), margem, 34, 9, false, SKColors.White.WithAlpha(215), espacamento: 0.9f);

        var y = 52f;
        foreach (var l in Quebrar(d.Titulo.ToUpper(Fmt.PtBr), 28, true, larguraTexto, out var tamTitulo, minimo: 20, maxLinhas: 2))
        {
            Escrever(c, l, margem, y, tamTitulo, true, SKColors.White);
            y += tamTitulo * 0.42f;
        }
        if (!string.IsNullOrWhiteSpace(d.Subtitulo))
        {
            y += 3;
            foreach (var l in Quebrar(d.Subtitulo, 13, false, larguraTexto, out var tamSub, minimo: 11, maxLinhas: 3))
            {
                Escrever(c, l, margem, y, tamSub, false, AzulClaro);
                y += tamSub * 0.5f;
            }
        }

        // ---------- brasão(ões) num cartão branco sobre a borda da faixa
        var yConteudo = alturaFaixa + 22f;
        if (temBrasao)
        {
            var imagens = d.Brasoes.Select(SKBitmap.Decode).Where(b => b != null).ToList();
            try
            {
                const float alturaBrasao = 38f, folga = 7f, entre = 10f;
                var larguras = imagens.Select(b => alturaBrasao * b.Width / b.Height).ToList();
                var larguraCartao = larguras.Sum() + entre * (imagens.Count - 1) + 2 * folga;
                var alturaCartao = alturaBrasao + 2 * folga;
                var x0 = ((float)LarguraMm - larguraCartao) / 2;
                var y0 = alturaFaixa - alturaCartao / 2;
                var cartao = new SKRoundRect(new SKRect(x0, y0, x0 + larguraCartao, y0 + alturaCartao), 3.5f);
                using (var sombra = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(45),
                           ImageFilter = SKImageFilter.CreateBlur(2.2f, 2.2f) })
                    c.DrawRoundRect(cartao.Rect.Left, cartao.Rect.Top + 1.2f, cartao.Width, cartao.Height, 3.5f, 3.5f, sombra);
                using (var branco = new SKPaint { IsAntialias = true, Color = SKColors.White }) c.DrawRoundRect(cartao, branco);
                var x = x0 + folga;
                using var pImg = new SKPaint { IsAntialias = true };
                for (int i = 0; i < imagens.Count; i++)
                {
                    using var img = SKImage.FromBitmap(imagens[i]);
                    c.DrawImage(img, new SKRect(x, y0 + folga, x + larguras[i], y0 + folga + alturaBrasao),
                        new SKSamplingOptions(SKCubicResampler.Mitchell), pImg);
                    x += larguras[i] + entre;
                }
                yConteudo = y0 + alturaCartao + 20f;
            }
            finally { foreach (var b in imagens) b.Dispose(); }
        }

        // ---------- município
        var meio = (float)LarguraMm / 2;
        // Uma linha sempre que possível (reduz a fonte até 17 pt); só quebra se ainda não couber
        foreach (var l in Quebrar(d.Municipio.ToUpper(Fmt.PtBr), 26, true, larguraTexto, out var tamMun, minimo: 17, maxLinhas: 1))
        {
            Escrever(c, l, meio, yConteudo, tamMun, true, Marinho, SKTextAlign.Center);
            yConteudo += tamMun * 0.45f;
        }
        Escrever(c, "ESTADO DE SÃO PAULO", meio, yConteudo + 1, 9.5f, false, Cinza, SKTextAlign.Center, espacamento: 1.1f);
        using (var p = new SKPaint { Color = Rosa }) c.DrawRect(meio - 9, yConteudo + 7, 18, 1.1f, p);

        // ---------- quadro de informações
        var linhas = new List<(string rotulo, string valor)>();
        if (!string.IsNullOrWhiteSpace(d.PeriodoAnalisado)) linhas.Add(("PERÍODO ANALISADO", d.PeriodoAnalisado));
        linhas.Add(("FONTE DOS DADOS", "Tribunal de Contas do Estado de São Paulo (TCESP)"));
        linhas.Add(("DATA DE EMISSÃO", emissao.ToString("d 'de' MMMM 'de' yyyy", Fmt.PtBr)));
        const float xRotulo = 32f, xValor = 82f, larguraValor = 210f - 32f - xValor;
        // Quadro na parte de baixo da página (alinhado acima do rodapé), sem colar no nome do município
        var yInfo = Math.Max(yConteudo + 30f, 222f - 13f * (linhas.Count - 1));
        using (var pLinha = new SKPaint { Color = Linha, StrokeWidth = 0.3f, IsAntialias = true })
        {
            c.DrawLine(xRotulo, yInfo - 7, (float)LarguraMm - xRotulo, yInfo - 7, pLinha);
            foreach (var (rotulo, valor) in linhas)
            {
                Escrever(c, rotulo, xRotulo, yInfo, 8, true, Cinza, espacamento: 0.6f);
                var yv = yInfo;
                foreach (var l in Quebrar(valor, 11.5f, false, larguraValor, out var tamV, minimo: 10, maxLinhas: 2))
                {
                    Escrever(c, l, xValor, yv, tamV, false, Texto);
                    yv += tamV * 0.48f;
                }
                yInfo = Math.Max(yInfo, yv - 11.5f * 0.48f) + 7f;
                c.DrawLine(xRotulo, yInfo - 4.2f, (float)LarguraMm - xRotulo, yInfo - 4.2f, pLinha);
                yInfo += 6f;
            }
        }

        // ---------- rodapé
        const float alturaRodape = 20f;
        var yRodape = (float)AlturaMm - alturaRodape;
        using (var p = new SKPaint { Color = MarinhoEscuro }) c.DrawRect(0, yRodape, (float)LarguraMm, alturaRodape, p);
        using (var p = new SKPaint { Color = Rosa }) c.DrawRect(0, yRodape, (float)LarguraMm, 1.1f, p);
        Escrever(c, o.Consultoria, margem, yRodape + 12.2f, 12, true, SKColors.White);
        var direita = string.IsNullOrWhiteSpace(o.Contato) ? "Relatório gerencial de finanças públicas municipais" : o.Contato;
        Escrever(c, direita, (float)LarguraMm - margem, yRodape + 12f, 8.5f, false, AzulClaro, SKTextAlign.Right);

        using var imagem = surface.Snapshot();
        using var png = imagem.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>Escreve texto com tamanho em pontos tipográficos (o canvas está em mm).</summary>
    static void Escrever(SKCanvas c, string texto, float x, float y, float pontos, bool negrito, SKColor cor,
        SKTextAlign alin = SKTextAlign.Left, float espacamento = 0)
    {
        using var fonte = new SKFont(Fonte(negrito), pontos * PxPt / PxMm) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        using var p = new SKPaint { Color = cor, IsAntialias = true };
        if (espacamento <= 0) { c.DrawText(texto, x, y, alin, fonte, p); return; }
        // Letras espaçadas (rótulos em caixa alta)
        var larguras = texto.Select(ch => fonte.MeasureText(ch.ToString()) + espacamento * fonte.Size * 0.12f).ToList();
        var total = larguras.Sum();
        var xi = alin switch { SKTextAlign.Center => x - total / 2, SKTextAlign.Right => x - total, _ => x };
        for (int i = 0; i < texto.Length; i++) { c.DrawText(texto[i].ToString(), xi, y, SKTextAlign.Left, fonte, p); xi += larguras[i]; }
    }

    /// <summary>Quebra o texto em linhas que cabem na largura (mm), reduzindo a fonte até o mínimo se precisar.</summary>
    static List<string> Quebrar(string texto, float pontos, bool negrito, float larguraMm, out float tamanho, float minimo, int maxLinhas)
    {
        tamanho = pontos;
        List<string> linhas;
        while (true)
        {
            using var fonte = new SKFont(Fonte(negrito), tamanho * PxPt / PxMm);
            linhas = [];
            var atual = "";
            foreach (var palavra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var teste = atual.Length == 0 ? palavra : atual + " " + palavra;
                if (fonte.MeasureText(teste) <= larguraMm || atual.Length == 0) atual = teste;
                else { linhas.Add(atual); atual = palavra; }
            }
            if (atual.Length > 0) linhas.Add(atual);
            if (linhas.Count <= maxLinhas || tamanho <= minimo) break;
            tamanho = Math.Max(minimo, tamanho - 1);
        }
        return linhas.Take(maxLinhas + 1).ToList();
    }
}
