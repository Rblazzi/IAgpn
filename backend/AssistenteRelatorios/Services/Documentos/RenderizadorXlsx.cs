using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using RelatorioIA.Api.Models;

namespace RelatorioIA.Api.Services.Documentos;

/// <summary>
/// Tabelas do relatório → Excel (.xlsx): uma aba por tabela, com título, cabeçalho, linhas e nota. Valores em R$ e %
/// viram NÚMEROS (com formato de moeda/percentual), para o usuário somar, filtrar e montar seus próprios gráficos.
/// </summary>
public static partial class RenderizadorXlsx
{
    // Estilos (índices de CellFormats, na ordem montada em Estilos())
    const uint Texto = 1, Cabecalho = 2, Moeda = 3, Percentual = 4, NumeroEstilo = 5, TextoNegrito = 6, MoedaNegrito = 7,
        PercentualNegrito = 8, NumeroNegrito = 9, Titulo = 10, Nota = 11;

    public static byte[]? Renderizar(Documento d)
    {
        var tabelas = new List<(string aba, TabelaDoc t)>();
        string? secao = null;
        var nomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in d.Blocos)
        {
            if (b is Titulo ti) secao = ti.Texto;
            if (b is not TabelaDoc t) continue;
            // Tabela larga impressa em blocos (JAN–MAR, ABR–JUN, JUL–TOTAL; anos em blocos): no Excel volta a ser UMA aba,
            // com as colunas lado a lado — o bloco sem título e com as mesmas linhas continua o anterior
            if (t.Titulo is null && tabelas.Count > 0 && MesmasLinhas(tabelas[^1].t, t))
            {
                tabelas[^1] = (tabelas[^1].aba, Juntar(tabelas[^1].t, t));
                continue;
            }
            var nome = NomeAba(t.Titulo ?? (tabelas.Count > 0 ? tabelas[^1].aba : secao) ?? "Tabela");
            var unico = nome;
            for (int n = 2; !nomes.Add(unico); n++) unico = $"{nome[..Math.Min(nome.Length, 26)]} ({n})";
            tabelas.Add((unico, t));
        }
        if (tabelas.Count == 0) return null;

        using var ms = new MemoryStream();
        using (var x = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = x.AddWorkbookPart();
            wb.Workbook = new Workbook();
            var estilos = wb.AddNewPart<WorkbookStylesPart>();
            estilos.Stylesheet = Estilos();
            var sheets = wb.Workbook.AppendChild(new Sheets());
            uint id = 1;
            foreach (var (aba, t) in tabelas)
            {
                var ws = wb.AddNewPart<WorksheetPart>();
                ws.Worksheet = Planilha(t);
                sheets.Append(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = id++, Name = aba });
            }
            wb.Workbook.Save();
        }
        return ms.ToArray();
    }

    static bool MesmasLinhas(TabelaDoc a, TabelaDoc b) =>
        a.Linhas.Count == b.Linhas.Count && a.Linhas.Count > 0 &&
        a.Linhas.Zip(b.Linhas).All(p => p.First.Length > 0 && p.Second.Length > 0 && p.First[0] == p.Second[0]);

    static TabelaDoc Juntar(TabelaDoc a, TabelaDoc b) => a with
    {
        Cabecalho = a.Cabecalho is null || b.Cabecalho is null ? a.Cabecalho : [.. a.Cabecalho, .. b.Cabecalho.Skip(1)],
        Linhas = a.Linhas.Zip(b.Linhas).Select(p => (string[])[.. p.First, .. p.Second.Skip(1)]).ToList(),
        Nota = b.Nota ?? a.Nota
    };

    static string NomeAba(string s)
    {
        var limpo = RegexInvalidos().Replace(s, " ").Trim();
        limpo = Regex.Replace(limpo, @"\s+", " ");
        return string.IsNullOrEmpty(limpo) ? "Tabela" : limpo.Length > 31 ? limpo[..31].TrimEnd() : limpo;
    }

    static Worksheet Planilha(TabelaDoc t)
    {
        var dados = new SheetData();
        uint linha = 1;
        var nCol = Math.Max(t.Cabecalho?.Length ?? 0, t.Linhas.Count > 0 ? t.Linhas.Max(l => l.Length) : 0);
        var larguras = new double[nCol];
        void Medir(int c, string v) { if (c < nCol) larguras[c] = Math.Max(larguras[c], Math.Min(60, v.Length * 1.1 + 2)); }

        if (t.Titulo is { } titulo) { dados.Append(new Row(TextoCel($"A{linha}", titulo, Titulo)) { RowIndex = linha }); linha += 2; }
        if (t.Cabecalho is { } cab)
        {
            var r = new Row { RowIndex = linha };
            for (int c = 0; c < cab.Length; c++) { r.Append(TextoCel(Ref(c, linha), cab[c], Cabecalho)); Medir(c, cab[c]); }
            dados.Append(r);
            linha++;
        }
        for (int i = 0; i < t.Linhas.Count; i++, linha++)
        {
            var negrito = t.Negrito(i);
            var r = new Row { RowIndex = linha };
            for (int c = 0; c < t.Linhas[i].Length; c++)
            {
                var v = t.Linhas[i][c];
                r.Append(Celula(Ref(c, linha), v, negrito));
                Medir(c, v);
            }
            dados.Append(r);
        }
        if (t.Nota is { } nota) { linha++; dados.Append(new Row(TextoCel($"A{linha}", nota, Nota)) { RowIndex = linha }); }

        var colunas = new Columns(larguras.Select((w, c) =>
            new Column { Min = (uint)c + 1, Max = (uint)c + 1, Width = Math.Max(10, w), CustomWidth = true }));
        return new Worksheet(colunas, dados);
    }

    /// <summary>"R$ 1.234,56" / "R$ (1.234,56)" → moeda; "12,34%" → percentual; "1.234" → número; o resto, texto.</summary>
    static Cell Celula(string referencia, string v, bool negrito)
    {
        var s = v.Trim();
        if (s.StartsWith("R$") && Numero(s.Replace("R$", "")) is { } m)
            return NumCel(referencia, m, negrito ? MoedaNegrito : Moeda);
        if (s.EndsWith('%') && Numero(s.TrimEnd('%')) is { } p)
            return NumCel(referencia, p / 100m, negrito ? PercentualNegrito : Percentual);
        // Anos e códigos ("2025", "17210000") ficam como texto; números com separador ("1.234", "12,5") viram número
        if (RegexNumero().IsMatch(s) && (s.Contains('.') || s.Contains(',')) && Numero(s) is { } n)
            return NumCel(referencia, n, negrito ? NumeroNegrito : NumeroEstilo);
        return TextoCel(referencia, v, negrito ? TextoNegrito : Texto);
    }

    static decimal? Numero(string s)
    {
        s = s.Trim();
        var neg = s.StartsWith('-') || s.StartsWith('(') && s.EndsWith(')');
        s = s.Trim('(', ')', '-', ' ');
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out var d) ? (neg ? -d : d) : null;
    }

    static Cell NumCel(string r, decimal v, uint estilo) =>
        new() { CellReference = r, CellValue = new CellValue(v.ToString(CultureInfo.InvariantCulture)), StyleIndex = estilo };

    static Cell TextoCel(string r, string v, uint estilo) =>
        new() { CellReference = r, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(v)), StyleIndex = estilo };

    static string Ref(int coluna, uint linha)
    {
        var letras = "";
        for (var c = coluna + 1; c > 0; c = (c - 1) / 26) letras = (char)('A' + (c - 1) % 26) + letras;
        return letras + linha;
    }

    static Stylesheet Estilos()
    {
        var borda = new Border(new LeftBorder { Style = BorderStyleValues.Thin }, new RightBorder { Style = BorderStyleValues.Thin },
            new TopBorder { Style = BorderStyleValues.Thin }, new BottomBorder { Style = BorderStyleValues.Thin }, new DiagonalBorder());
        CellFormat F(uint fonte, uint fundo, uint bordaId, uint formato = 0, HorizontalAlignmentValues? alin = null) => new()
        {
            FontId = fonte, FillId = fundo, BorderId = bordaId, NumberFormatId = formato,
            ApplyFont = true, ApplyFill = fundo > 0, ApplyBorder = bordaId > 0, ApplyNumberFormat = formato > 0,
            Alignment = alin is null ? null : new Alignment { Horizontal = alin, Vertical = VerticalAlignmentValues.Center, WrapText = true }
        };
        var centro = HorizontalAlignmentValues.Center;
        return new Stylesheet(
            new NumberingFormats(
                new NumberingFormat { NumberFormatId = 164, FormatCode = "\"R$\" #,##0.00;[Red]\"R$\" -#,##0.00" },
                new NumberingFormat { NumberFormatId = 165, FormatCode = "0.00%;[Red]-0.00%" },
                new NumberingFormat { NumberFormatId = 166, FormatCode = "#,##0.##" }) { Count = 3 },
            new Fonts(
                new Font(new FontSize { Val = 10 }, new FontName { Val = "Arial" }),
                new Font(new Bold(), new FontSize { Val = 10 }, new FontName { Val = "Arial" }),
                new Font(new Bold(), new FontSize { Val = 12 }, new Color { Rgb = "FF2E3192" }, new FontName { Val = "Arial" }),
                new Font(new Italic(), new FontSize { Val = 9 }, new FontName { Val = "Arial" })),
            new Fills(
                new Fill(new PatternFill { PatternType = PatternValues.None }),
                new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFF2F2F2" }) { PatternType = PatternValues.Solid })),
            new Borders(new Border(), borda),
            new CellFormats(
                F(0, 0, 0),                          // 0 padrão
                F(0, 0, 1),                          // 1 texto
                F(1, 2, 1, 0, centro),               // 2 cabeçalho
                F(0, 0, 1, 164, centro),             // 3 moeda
                F(0, 0, 1, 165, centro),             // 4 percentual
                F(0, 0, 1, 166, centro),             // 5 número
                F(1, 0, 1),                          // 6 texto negrito
                F(1, 0, 1, 164, centro),             // 7 moeda negrito
                F(1, 0, 1, 165, centro),             // 8 percentual negrito
                F(1, 0, 1, 166, centro),             // 9 número negrito
                F(2, 0, 0),                          // 10 título
                F(3, 0, 0)));                        // 11 nota
    }

    [GeneratedRegex(@"[\[\]\:\*\?\/\\]")] private static partial Regex RegexInvalidos();
    [GeneratedRegex(@"^-?\d{1,3}(\.\d{3})*(,\d+)?$|^-?\d+(,\d+)?$")] private static partial Regex RegexNumero();
}
