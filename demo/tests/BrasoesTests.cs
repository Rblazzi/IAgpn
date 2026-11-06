using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Models;
using RelatorioIA.Api.Services.Documentos;

namespace RelatorioIA.Tests;

/// <summary>Brasão na capa: busca no Wikidata (internet) com cache em disco, e renderização em Word/PDF.</summary>
public class BrasoesTests
{
    static readonly IOptions<RelatorioOptions> Opt = Options.Create(new RelatorioOptions());

    sealed class Ambiente(string raiz) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = raiz;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "testes";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = raiz;
        public string EnvironmentName { get; set; } = "Development";
    }

    static (BrasaoWikidata fonte, string pasta) Fonte()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "brasoes-teste-" + Guid.NewGuid().ToString("N")[..8]);
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AssistenteRelatorios-testes/1.0");
        var fonte = new BrasaoWikidata(http, new MemoryCache(new MemoryCacheOptions()), Opt, new Ambiente(raiz),
            new ConfigurationBuilder().Build(), NullLogger<BrasaoWikidata>.Instance);
        return (fonte, Path.Combine(raiz, "brasoes"));
    }

    static bool EhPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G';

    [Fact]
    public async Task Baixa_o_brasao_do_Wikidata_e_guarda_em_cache()
    {
        var (fonte, pasta) = Fonte();
        var png = await fonte.ObterAsync("Águas da Prata");
        Assert.NotNull(png);
        Assert.True(EhPng(png));
        Assert.True(File.Exists(Path.Combine(pasta, "aguas-da-prata.png")));
        // 2ª vez vem do disco
        Assert.Equal(png, await fonte.ObterAsync("aguas da prata"));
    }

    [Fact]
    public async Task Municipio_inexistente_nao_quebra_o_relatorio()
    {
        var (fonte, _) = Fonte();
        Assert.Null(await fonte.ObterAsync("Cidade Que Não Existe"));
    }

    [Theory]
    [InlineData("Santa Bárbara d'Oeste", "santa barbara d oeste")]
    [InlineData("SÃO JOÃO DA BOA VISTA", "sao joao da boa vista")]
    public void Normaliza_nomes(string nome, string esperado) => Assert.Equal(esperado, BrasaoWikidata.Normalizar(nome));

    [Fact]
    public void Capa_com_brasao_gera_Word_e_PDF_com_a_imagem()
    {
        using var bmp = new SkiaSharp.SKBitmap(200, 240);
        bmp.Erase(SkiaSharp.SKColors.SteelBlue);
        var png = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
        var doc = new Documento { Titulo = "RELATÓRIO GERENCIAL", Municipio = "Águas da Prata" };
        doc.Brasoes.Add(png);
        doc.T1("Teste").P("Texto.");

        var docx = new RenderizadorDocx(Opt).Renderizar(doc);
        using var w = WordprocessingDocument.Open(new MemoryStream(docx), false);
        Assert.Single(w.MainDocumentPart!.ImageParts);

        var pdf = new RenderizadorPdf(Opt).Renderizar(doc);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        // capa continua em 1 página: capa + sumário + conteúdo = 3 páginas
        Assert.Equal(3, PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(pdf), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import).PageCount);
    }
}
