using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Models;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace RelatorioIA.Api.Services.Documentos;

/// <summary>Documento padrão → Word (.docx, editável), visual do modelo da consultoria.</summary>
public class RenderizadorDocx(IOptions<RelatorioOptions> opt)
{
    public const string Azul = "2E3192", Rosa = "E8407A", AzulClaro = "9CCBFA", Fonte = "Arial";

    public byte[] Renderizar(Documento d)
    {
        var body = new Body();
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(body);
            CriarEstilos(main);
            var (hdrId, ftrId) = CriarCabecalhoRodape(main);
            main.AddNewPart<DocumentSettingsPart>().Settings = new Settings(new UpdateFieldsOnOpen { Val = d.Sumario });

            Capa(body, main, d);
            if (d.Sumario) Sumario(body);
            uint idImagem = 0;

            foreach (var b in d.Blocos)
            {
                switch (b)
                {
                    case Titulo t:
                        body.Append(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = $"Heading{t.Nivel}" }), new Run(new Text(t.Texto))));
                        break;
                    case Paragrafo p:
                        var recuo = p.Alinhamento == Alinhamento.Justificado && p.Texto.Length > 0;
                        var par = P(R(p.Texto, p.Negrito, p.Italico, (int)(p.Tamanho * 2)), Alin(p.Alinhamento), recuo: recuo,
                            manterProximo: p.ManterComProximo);
                        // Bloco de assinatura: também não divide o parágrafo entre páginas
                        if (p.ManterComProximo) par.ParagraphProperties!.KeepNext!.InsertAfterSelf(new KeepLines());
                        body.Append(par);
                        break;
                    case TabelaDoc tab:
                        Tabela(body, tab);
                        break;
                    case GraficoDoc graf:
                        Grafico(body, main, graf, ++idImagem);
                        break;
                    case QuebraPagina:
                        body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                        break;
                }
            }

            body.Append(new SectionProperties(
                new HeaderReference { Type = HeaderFooterValues.Default, Id = hdrId },
                new FooterReference { Type = HeaderFooterValues.Default, Id = ftrId },
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1418, Bottom = 1134, Left = 1418, Right = 1134, Header = 567, Footer = 567 }));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Capa: página inteira desenhada (<see cref="CapaDesenho"/>), numa seção própria sem margens e sem cabeçalho;
    /// a quebra de seção fica no mesmo parágrafo da imagem (não sobra página em branco).
    /// </summary>
    void Capa(Body body, MainDocumentPart main, Documento d)
    {
        const long emuPorMm = 36000;
        var png = CapaDesenho.Desenhar(d, opt.Value, DateTime.Now);
        var desenho = Imagem(main, png, (long)(CapaDesenho.LarguraMm * emuPorMm), (long)((CapaDesenho.AlturaMm - 1) * emuPorMm), 900, "Capa");
        body.Append(new Paragraph(
            new ParagraphProperties(
                new SpacingBetweenLines { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto },
                new SectionProperties(
                    new PageSize { Width = 11906, Height = 16838 },
                    new PageMargin { Top = 0, Bottom = 0, Left = 0, Right = 0, Header = 0, Footer = 0 })),
            new Run(desenho)));
    }

    static void Sumario(Body body)
    {
        body.Append(new Paragraph(
            new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" TOC \\o \"1-3\" \\h \\z \\u ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            R("Sumário: ao abrir, confirme a atualização dos campos (ou clique com o botão direito > Atualizar campo).", italico: true),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
        body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
    }

    static void Tabela(Body body, TabelaDoc t)
    {
        var fonte = (int)Math.Round(t.Fonte * 2);
        if (t.Titulo != null)
            body.Append(P(R(t.Titulo, negrito: true, tamanho: 18), JustificationValues.Center, antes: 240, depois: 60, manterProximo: true));

        var tbl = new Table(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 6 }, new BottomBorder { Val = BorderValues.Single, Size = 6 },
                new LeftBorder { Val = BorderValues.Single, Size = 6 }, new RightBorder { Val = BorderValues.Single, Size = 6 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableCellMarginDefault(new TopMargin { Width = "20", Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "20", Type = TableWidthUnitValues.Dxa },
                new TableCellLeftMargin { Width = 60, Type = TableWidthValues.Dxa },
                new TableCellRightMargin { Width = 60, Type = TableWidthValues.Dxa })));
        tbl.Append(new TableGrid(t.Larguras.Select(w => new GridColumn { Width = (w * 93).ToString() })));

        // Tabelas pequenas ficam inteiras na mesma página (evita cabeçalho "órfão" no fim da página)
        var manterJunto = t.Linhas.Count <= 15;

        TableRow Linha(string[] celulas, bool negrito, bool cab, bool ultima)
        {
            var tr = new TableRow(cab ? new TableRowProperties(new TableHeader(), new CantSplit()) : new TableRowProperties(new CantSplit()));
            for (int i = 0; i < celulas.Length; i++)
            {
                var alin = cab ? JustificationValues.Center
                    : i == 0 ? (t.CentralizarPrimeira ? JustificationValues.Center : JustificationValues.Left)
                    : JustificationValues.Center;   // valores centralizados (padrão da consultoria)
                var tcp = new TableCellProperties(
                    new TableCellWidth { Type = TableWidthUnitValues.Pct, Width = (t.Larguras[Math.Min(i, t.Larguras.Length - 1)] * 50).ToString() },
                    new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
                if (cab) tcp.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F2F2" });
                tr.Append(new TableCell(tcp, P(R(celulas[i], negrito: negrito || cab, tamanho: fonte), alin, antes: 0, depois: 0,
                    entrelinhas: 240, manterProximo: (cab || manterJunto) && !ultima)));
            }
            return tr;
        }

        if (t.Cabecalho != null) tbl.Append(Linha(t.Cabecalho, true, true, false));
        for (int i = 0; i < t.Linhas.Count; i++) tbl.Append(Linha(t.Linhas[i], t.Negrito(i), false, i == t.Linhas.Count - 1));
        body.Append(tbl);
        if (t.Nota != null) body.Append(P(R(t.Nota, italico: true, tamanho: 16), JustificationValues.Left, antes: 40, depois: 0));
        body.Append(P(R(""), antes: 0, depois: 120));
    }

    /// <summary>Gráfico como imagem PNG embutida (inline), na largura útil da página, com título acima.</summary>
    static void Grafico(Body body, MainDocumentPart main, GraficoDoc g, uint id)
    {
        body.Append(P(R(g.Titulo, negrito: true, tamanho: 18), JustificationValues.Center, antes: 240, depois: 60, manterProximo: true));

        const long emuPorCm = 360000;
        long cx = (long)(16.0 * emuPorCm), cy = (long)(16.0 * g.Proporcao * emuPorCm);
        var desenho = Imagem(main, g.Png, cx, cy, id, g.Titulo);

        body.Append(new Paragraph(
            new ParagraphProperties(new KeepNext(), new Justification { Val = JustificationValues.Center },
                new SpacingBetweenLines { Before = "0", After = "60", Line = "240", LineRule = LineSpacingRuleValues.Auto }),
            new Run(desenho)));
        if (g.Nota != null) body.Append(P(R(g.Nota, italico: true, tamanho: 16), JustificationValues.Left, antes: 0, depois: 0));
        body.Append(P(R(""), antes: 0, depois: 120));
    }

    /// <summary>PNG embutido inline (tamanho em EMU).</summary>
    static Drawing Imagem(MainDocumentPart main, byte[] png, long cx, long cy, uint id, string descricao)
    {
        var img = main.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(png)) img.FeedData(ms);
        var relId = main.GetIdOfPart(img);
        return new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = id, Name = $"Imagem {id}", Description = descricao },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = $"imagem{id}.png" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });
    }

    static JustificationValues Alin(Alinhamento a) => a switch
    {
        Alinhamento.Centro => JustificationValues.Center,
        Alinhamento.Direita => JustificationValues.Right,
        Alinhamento.Esquerda => JustificationValues.Left,
        _ => JustificationValues.Both
    };

    static Run R(string texto, bool negrito = false, bool italico = false, int tamanho = 22, string? cor = null)
    {
        var rp = new RunProperties(new RunFonts { Ascii = Fonte, HighAnsi = Fonte, ComplexScript = Fonte });
        if (negrito) rp.Append(new Bold());
        if (italico) rp.Append(new Italic());
        if (cor != null) rp.Append(new Color { Val = cor });
        rp.Append(new FontSize { Val = tamanho.ToString() });
        return new Run(rp, new Text(texto) { Space = SpaceProcessingModeValues.Preserve });
    }

    static Paragraph P(Run run, JustificationValues? alin = null, int antes = 0, int depois = 160, bool recuo = false,
        int entrelinhas = 360, bool bordaInferior = false, string? sombra = null, bool manterProximo = false)
    {
        var pp = new ParagraphProperties();
        if (manterProximo) pp.Append(new KeepNext());
        if (bordaInferior) pp.Append(new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 8, Color = "808080", Space = 12 }));
        if (sombra != null) pp.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = sombra });
        pp.Append(new SpacingBetweenLines { Before = antes.ToString(), After = depois.ToString(), Line = entrelinhas.ToString(), LineRule = LineSpacingRuleValues.Auto });
        if (recuo) pp.Append(new Indentation { FirstLine = "709" });
        pp.Append(new Justification { Val = alin ?? JustificationValues.Both });
        return new Paragraph(pp, run);
    }

    static void CriarEstilos(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        Style Titulo(string id, string nome, int tam, int antes, int nivel) => new(
            new StyleName { Val = nome }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" },
            new PrimaryStyle(),
            new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { Before = antes.ToString(), After = "160" },
                new OutlineLevel { Val = nivel }),
            new StyleRunProperties(new RunFonts { Ascii = Fonte, HighAnsi = Fonte }, new Color { Val = Azul }, new FontSize { Val = tam.ToString() }))
        { Type = StyleValues.Paragraph, StyleId = id };

        part.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = Fonte, HighAnsi = Fonte, ComplexScript = Fonte },
                    new FontSize { Val = "22" }, new Languages { Val = "pt-BR" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "160", Line = "276", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            Titulo("Heading1", "heading 1", 34, 360, 0),
            Titulo("Heading2", "heading 2", 30, 320, 1),
            Titulo("Heading3", "heading 3", 26, 240, 2));
        part.Styles.Save();
    }

    static (string hdr, string ftr) CriarCabecalhoRodape(MainDocumentPart main)
    {
        var h = main.AddNewPart<HeaderPart>();
        h.Header = new Header(new Paragraph(new ParagraphProperties(
            new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 36, Color = Rosa, Space = 1 }),
            new SpacingBetweenLines { After = "240" }), new Run(new Text(""))));
        h.Header.Save();

        var f = main.AddNewPart<FooterPart>();
        f.Footer = new Footer(new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Right }),
            new Run(new RunProperties(new Color { Val = "808080" }, new FontSize { Val = "18" }), new FieldChar { FieldCharType = FieldCharValues.Begin }),
            new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
            new Run(new RunProperties(new Color { Val = "808080" }, new FontSize { Val = "18" }), new Text("1")),
            new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
        f.Footer.Save();
        return (main.GetIdOfPart(h), main.GetIdOfPart(f));
    }
}
