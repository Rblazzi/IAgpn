using Microsoft.Extensions.Options;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using RelatorioIA.Api.Models;
using DocMigra = MigraDoc.DocumentObjectModel.Document;

namespace RelatorioIA.Api.Services.Documentos;

/// <summary>Documento padrão → PDF (MigraDoc/PDFsharp, licença MIT), mesmo visual do Word.</summary>
public class RenderizadorPdf(IOptions<RelatorioOptions> opt)
{
    static readonly Color Azul = Color.FromRgb(0x2E, 0x31, 0x92);
    static readonly Color Rosa = Color.FromRgb(0xE8, 0x40, 0x7A);
    static readonly Color AzulClaro = Color.FromRgb(0x9C, 0xCB, 0xFA);
    static readonly Color Cinza = Color.FromRgb(0x80, 0x80, 0x80);
    static readonly Color CinzaClaro = Color.FromRgb(0xF2, 0xF2, 0xF2);
    const double LarguraUtilCm = 16.5; // A4 21cm - margens 2,5 + 2,0

    static RenderizadorPdf()
    {
        // Usa as fontes instaladas no Windows (servidor IIS). Em Linux, configurar um IFontResolver.
        if (OperatingSystem.IsWindows()) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    public byte[] Renderizar(Models.Documento d)
    {
        var doc = new DocMigra();
        doc.Info.Title = $"{d.Titulo} - {d.Municipio}";
        doc.Info.Author = opt.Value.Consultoria;
        Estilos(doc);

        // Capa: página inteira desenhada (seção própria, sem margens, sem cabeçalho/rodapé)
        var capa = doc.AddSection();
        capa.PageSetup = doc.DefaultPageSetup.Clone();
        capa.PageSetup.PageFormat = PageFormat.A4;
        capa.PageSetup.TopMargin = capa.PageSetup.BottomMargin = capa.PageSetup.LeftMargin = capa.PageSetup.RightMargin = 0;
        capa.PageSetup.HeaderDistance = capa.PageSetup.FooterDistance = 0;
        var pc = capa.AddParagraph();
        pc.Format.LineSpacingRule = LineSpacingRule.Single;
        pc.Format.SpaceBefore = pc.Format.SpaceAfter = 0;
        var imgCapa = pc.AddImage("base64:" + Convert.ToBase64String(ParaJpeg(CapaDesenho.Desenhar(d, opt.Value, DateTime.Now), 94)));
        imgCapa.LockAspectRatio = false;
        imgCapa.Width = Unit.FromMillimeter(CapaDesenho.LarguraMm);
        imgCapa.Height = Unit.FromMillimeter(CapaDesenho.AlturaMm - 1);   // 1 mm de folga: não empurra para outra página

        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        sec.PageSetup.TopMargin = "2.5cm"; sec.PageSetup.BottomMargin = "2cm";
        sec.PageSetup.LeftMargin = "2.5cm"; sec.PageSetup.RightMargin = "2cm";
        sec.PageSetup.HeaderDistance = "1cm"; sec.PageSetup.FooterDistance = "1cm";

        var hdr = sec.Headers.Primary.AddParagraph();
        hdr.Format.Borders.Bottom = new Border { Width = 4.5, Color = Rosa };
        var ftr = sec.Footers.Primary.AddParagraph();
        ftr.Format.Alignment = ParagraphAlignment.Right;
        ftr.Format.Font.Size = 9; ftr.Format.Font.Color = Cinza;
        ftr.AddPageField();

        // Sumário com número de página (PageRef para bookmarks dos títulos)
        var titulos = d.Blocos.OfType<Titulo>().Select((t, i) => (t, id: $"t{i}")).ToList();
        if (d.Sumario && titulos.Count > 0)
        {
            sec.AddParagraph("Sumário", "Heading1").Format.OutlineLevel = OutlineLevel.BodyText;
            foreach (var (t, id) in titulos)
            {
                var p = sec.AddParagraph();
                p.Format.LeftIndent = Unit.FromCentimeter(0.6 * (t.Nivel - 1));
                p.Format.SpaceAfter = 3;
                p.Format.TabStops.AddTabStop(Unit.FromCentimeter(LarguraUtilCm), TabAlignment.Right, TabLeader.Dots);
                var link = p.AddHyperlink(id);
                link.AddText(t.Texto);
                link.AddTab();
                link.AddPageRefField(id);
                p.Format.Font.Color = Color.FromRgb(0x11, 0x55, 0xCC);
            }
            sec.AddPageBreak();
        }

        // Gráficos vão para o PDF em JPEG (qualidade alta, fundo branco): o PDFsharp incorpora JPEG direto, enquanto
        // cada PNG era decodificado e recomprimido (~100 ms por gráfico). Conversão em paralelo.
        var jpegs = d.Blocos.OfType<GraficoDoc>().Distinct().AsParallel()
            .ToDictionary(g => g, g => Convert.ToBase64String(ParaJpeg(g.Png)));

        var idTitulo = 0;
        foreach (var b in d.Blocos)
        {
            switch (b)
            {
                case Titulo t:
                    var h = sec.AddParagraph(t.Texto, $"Heading{t.Nivel}");
                    h.AddBookmark($"t{idTitulo++}");
                    break;
                case Paragrafo p:
                    var par = sec.AddParagraph();
                    var txt = par.AddFormattedText(p.Texto);
                    txt.Bold = p.Negrito; txt.Italic = p.Italico; txt.Size = p.Tamanho;
                    par.Format.Alignment = p.Alinhamento switch
                    {
                        Alinhamento.Centro => ParagraphAlignment.Center,
                        Alinhamento.Direita => ParagraphAlignment.Right,
                        Alinhamento.Esquerda => ParagraphAlignment.Left,
                        _ => ParagraphAlignment.Justify
                    };
                    if (p.Alinhamento == Alinhamento.Justificado && p.Texto.Length > 0) par.Format.FirstLineIndent = "1.25cm";
                    if (p.ManterComProximo) { par.Format.KeepWithNext = true; par.Format.KeepTogether = true; }
                    break;
                case TabelaDoc tab:
                    Tabela(sec, tab);
                    break;
                case GraficoDoc graf:
                    var pt = sec.AddParagraph(graf.Titulo);
                    pt.Format.Font.Bold = true; pt.Format.Font.Size = 9; pt.Format.Alignment = ParagraphAlignment.Center;
                    pt.Format.SpaceBefore = 12; pt.Format.SpaceAfter = 3; pt.Format.KeepWithNext = true;
                    var pi = sec.AddParagraph();
                    pi.Format.Alignment = ParagraphAlignment.Center;
                    // Espaçamento simples: com 1,5 (estilo Normal) a altura da linha vira 1,5× a imagem
                    pi.Format.LineSpacingRule = LineSpacingRule.Single;
                    pi.Format.SpaceAfter = 0;
                    var img = pi.AddImage("base64:" + jpegs[graf]);
                    img.Width = Unit.FromCentimeter(16);
                    img.LockAspectRatio = true;
                    if (graf.Nota != null)
                    {
                        var n = sec.AddParagraph(graf.Nota);
                        n.Format.Font.Size = 8; n.Format.Font.Italic = true;
                    }
                    sec.AddParagraph().Format.SpaceAfter = 4;
                    break;
                case QuebraPagina:
                    sec.AddPageBreak();
                    break;
            }
        }

        var r = new PdfDocumentRenderer { Document = doc };
        r.RenderDocument();
        using var ms = new MemoryStream();
        r.PdfDocument.Save(ms, false);
        return ms.ToArray();
    }

    static byte[] ParaJpeg(byte[] png, int qualidade = 92)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        if (bmp is null) return png;
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var dados = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, qualidade);
        return dados.ToArray();
    }

    static void Tabela(Section sec, TabelaDoc t)
    {
        if (t.Titulo != null)
        {
            var pt = sec.AddParagraph(t.Titulo);
            pt.Format.Font.Bold = true; pt.Format.Font.Size = 9; pt.Format.Alignment = ParagraphAlignment.Center;
            pt.Format.SpaceBefore = 12; pt.Format.SpaceAfter = 3; pt.Format.KeepWithNext = true;
        }

        var tbl = sec.AddTable();
        tbl.Borders.Width = 0.5;
        tbl.Format.Font.Size = t.Fonte;
        // Células compactas: espaçamento simples, sem espaço antes/depois (o estilo Normal usa 1,5 linha + 6 pt,
        // o que deixava cada linha da tabela alta demais)
        tbl.Format.LineSpacingRule = LineSpacingRule.Single;
        tbl.Format.SpaceBefore = 0;
        tbl.Format.SpaceAfter = 0;
        tbl.TopPadding = 2; tbl.BottomPadding = 2; tbl.LeftPadding = 3; tbl.RightPadding = 3;
        foreach (var w in t.Larguras) tbl.AddColumn(Unit.FromCentimeter(LarguraUtilCm * w / 100.0));

        void Linha(string[] celulas, bool negrito, bool cab)
        {
            var row = tbl.AddRow();
            row.VerticalAlignment = VerticalAlignment.Center;
            if (cab) { row.HeadingFormat = true; row.Shading.Color = CinzaClaro; }
            for (int i = 0; i < celulas.Length && i < t.Larguras.Length; i++)
            {
                var p = row.Cells[i].AddParagraph(celulas[i]);
                p.Format.LineSpacingRule = LineSpacingRule.Single;
                p.Format.SpaceBefore = 0; p.Format.SpaceAfter = 0;
                p.Format.Font.Bold = negrito || cab;
                p.Format.Alignment = cab ? ParagraphAlignment.Center
                    : i == 0 ? (t.CentralizarPrimeira ? ParagraphAlignment.Center : ParagraphAlignment.Left)
                    : ParagraphAlignment.Center;   // valores centralizados (padrão da consultoria)
            }
        }

        if (t.Cabecalho != null) Linha(t.Cabecalho, true, true);
        for (int i = 0; i < t.Linhas.Count; i++) Linha(t.Linhas[i], t.Negrito(i), false);
        // Tabelas pequenas ficam inteiras na mesma página
        if (tbl.Rows.Count <= 16 && tbl.Rows.Count > 1) tbl.Rows[0].KeepWith = tbl.Rows.Count - 1;

        if (t.Nota != null)
        {
            var n = sec.AddParagraph(t.Nota);
            n.Format.Font.Size = 8; n.Format.Font.Italic = true; n.Format.SpaceBefore = 2;
        }
        sec.AddParagraph().Format.SpaceAfter = 4;
    }

    static void Estilos(DocMigra doc)
    {
        var n = doc.Styles[StyleNames.Normal]!;
        n.Font.Name = "Arial"; n.Font.Size = 11;
        n.ParagraphFormat.LineSpacingRule = LineSpacingRule.OnePtFive;
        n.ParagraphFormat.SpaceAfter = 6;

        void H(string nome, double tam, double antes, OutlineLevel nivel)
        {
            var s = doc.Styles[nome]!;
            s.Font.Name = "Arial"; s.Font.Size = tam; s.Font.Color = Azul; s.Font.Bold = false;
            s.ParagraphFormat.SpaceBefore = antes; s.ParagraphFormat.SpaceAfter = 8;
            s.ParagraphFormat.KeepWithNext = true; s.ParagraphFormat.OutlineLevel = nivel;
            s.ParagraphFormat.LineSpacingRule = LineSpacingRule.Single;
        }
        H(StyleNames.Heading1, 17, 18, OutlineLevel.Level1);
        H(StyleNames.Heading2, 15, 16, OutlineLevel.Level2);
        H(StyleNames.Heading3, 13, 12, OutlineLevel.Level3);
    }
}
