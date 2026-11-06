using RelatorioIA.Api.Models;
using RelatorioIA.Api.Services.Documentos;

namespace RelatorioIA.Tests;

/// <summary>Capa desenhada: gera a página A4 com e sem brasão e grava amostras para conferência visual.</summary>
public class CapaTests
{
    [Theory]
    [InlineData("RELATÓRIO GERENCIAL", "Águas da Prata", 1)]
    [InlineData("RELATÓRIO COMPARATIVO", "Águas da Prata × São João da Boa Vista", 2)]
    [InlineData("RELATÓRIO GERENCIAL", "São João da Boa Vista", 0)]
    public void Desenha_capa_A4(string titulo, string municipio, int brasoes)
    {
        var doc = new Documento
        {
            Titulo = titulo, Municipio = municipio, PeriodoAnalisado = "Janeiro a Julho de 2026",
            Subtitulo = "Análise financeira sobre as contas públicas municipais, com comparativo entre exercícios"
        };
        for (int i = 0; i < brasoes; i++)
        {
            using var bmp = new SkiaSharp.SKBitmap(200, 240);
            bmp.Erase(i == 0 ? SkiaSharp.SKColors.SteelBlue : SkiaSharp.SKColors.IndianRed);
            doc.Brasoes.Add(SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray());
        }
        var png = CapaDesenho.Desenhar(doc, new RelatorioOptions(), new DateTime(2026, 11, 5));
        using var img = SkiaSharp.SKBitmap.Decode(png);
        Assert.Equal(1654, img.Width);
        Assert.InRange(img.Height, 2338, 2340);
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), $"capa-teste-{brasoes}.png"), png);
    }
}
