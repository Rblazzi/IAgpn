using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Services.Documentos;
using RelatorioIA.Api.Services.IA;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace RelatorioIA.Api.Services;

public record AnexoSalvo(string Id, string Nome, long Tamanho);

/// <summary>
/// Arquivos anexados no chat: guardados em {Relatorio:Pasta}/anexos/ (apagados junto com os relatórios antigos) e lidos
/// para a IA. Word, Excel, CSV e TXT viram texto; PDF e imagens vão inteiros para o modelo (que lê o conteúdo).
/// </summary>
public class Anexos(IOptions<RelatorioOptions> opt, IWebHostEnvironment env, ILogger<Anexos> log)
{
    public const long TamanhoMaximo = 10 * 1024 * 1024;
    public const int MaximoPorPedido = 5;
    const int MaxCaracteres = 60_000;   // texto extraído por arquivo (planilhas grandes são cortadas)

    /// <summary>Extensões aceitas → tipo MIME enviado ao modelo (null = vira texto).</summary>
    public static readonly IReadOnlyDictionary<string, string?> Tipos = new Dictionary<string, string?>
    {
        [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".docx"] = null, [".xlsx"] = null, [".csv"] = null, [".txt"] = null
    };

    public string Pasta => Path.GetFullPath(Path.Combine(env.ContentRootPath, opt.Value.Pasta, "anexos"));

    public static bool IdValido(string? id) => id is { Length: 12 } && id.All(Uri.IsHexDigit);

    /// <summary>Valida (extensão, tamanho e assinatura do arquivo) e salva. Devolve o erro, se houver.</summary>
    public async Task<(AnexoSalvo? anexo, string? erro)> SalvarAsync(string nomeOriginal, Stream conteudo, long tamanho, CancellationToken ct)
    {
        var ext = Path.GetExtension(nomeOriginal).ToLowerInvariant();
        if (!Tipos.ContainsKey(ext)) return (null, "Tipo de arquivo não aceito. Use PDF, Word (.docx), Excel (.xlsx), CSV, TXT, PNG ou JPG.");
        if (tamanho <= 0) return (null, "Arquivo vazio.");
        if (tamanho > TamanhoMaximo) return (null, "Arquivo muito grande (máximo 10 MB).");

        using var ms = new MemoryStream();
        await conteudo.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        if (!AssinaturaConfere(ext, bytes)) return (null, "O conteúdo do arquivo não corresponde à extensão.");

        Directory.CreateDirectory(Pasta);
        var id = Guid.NewGuid().ToString("N")[..12];
        var invalidos = Path.GetInvalidFileNameChars();
        var nome = new string(Path.GetFileName(nomeOriginal).Select(c => invalidos.Contains(c) ? '-' : c).ToArray());
        if (nome.Length > 100) nome = nome[..(100 - ext.Length)] + ext;
        await File.WriteAllBytesAsync(Path.Combine(Pasta, $"{id}__{nome}"), bytes, ct);
        log.LogInformation("Anexo recebido: {Nome} ({Tamanho} bytes) → {Id}", nome, bytes.Length, id);
        return (new AnexoSalvo(id, nome, bytes.Length), null);
    }

    /// <summary>Confere os primeiros bytes (evita, por exemplo, um executável renomeado para .pdf).</summary>
    public static bool AssinaturaConfere(string ext, byte[] b) => ext switch
    {
        ".pdf" => b.Length > 4 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F',
        ".png" => b.Length > 8 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G',
        ".jpg" or ".jpeg" => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8,
        ".docx" or ".xlsx" => b.Length > 4 && b[0] == 'P' && b[1] == 'K',
        _ => !b.Take(4096).Contains((byte)0)   // csv/txt: texto puro
    };

    /// <summary>Carrega os anexos pelos ids (ignora ids inválidos ou arquivos já apagados).</summary>
    public async Task<List<AnexoConteudo>> CarregarAsync(IEnumerable<string>? ids, CancellationToken ct)
    {
        var lista = new List<AnexoConteudo>();
        if (ids is null || !Directory.Exists(Pasta)) return lista;
        foreach (var id in ids.Where(IdValido).Distinct().Take(MaximoPorPedido))
        {
            var arquivo = Directory.GetFiles(Pasta, $"{id}__*").FirstOrDefault();
            if (arquivo is null) { log.LogWarning("Anexo {Id} não encontrado (apagado?)", id); continue; }
            var nome = Path.GetFileName(arquivo)[(id.Length + 2)..];
            var ext = Path.GetExtension(nome).ToLowerInvariant();
            try
            {
                var bytes = await File.ReadAllBytesAsync(arquivo, ct);
                lista.Add(Tipos.TryGetValue(ext, out var mime) && mime is not null
                    ? new AnexoConteudo(id, nome, null, mime, bytes)
                    : new AnexoConteudo(id, nome, Cortar(ExtrairTexto(ext, bytes)), null, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Não foi possível ler o anexo {Nome}", nome);
                lista.Add(new AnexoConteudo(id, nome, "(não foi possível ler o conteúdo deste arquivo)", null, null));
            }
        }
        return lista;
    }

    static string Cortar(string s) => s.Length <= MaxCaracteres ? s : s[..MaxCaracteres] + "\n[... arquivo cortado: conteúdo muito longo]";

    public static string ExtrairTexto(string ext, byte[] bytes) => ext switch
    {
        ".docx" => TextoWord(bytes),
        ".xlsx" => TextoExcel(bytes),
        _ => TextoPuro(bytes)
    };

    static string TextoPuro(byte[] bytes)
    {
        // UTF-8; se não for válido, Latin-1 (CSV exportado pelo Excel em português)
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>Parágrafos e tabelas do Word, na ordem (tabelas com células separadas por " | ").</summary>
    static string TextoWord(byte[] bytes)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var sb = new StringBuilder();
        foreach (var el in doc.MainDocumentPart?.Document.Body?.ChildElements ?? [])
        {
            if (el is W.Paragraph p) { if (p.InnerText.Trim() is { Length: > 0 } t) sb.AppendLine(t); }
            else if (el is W.Table tab)
            {
                sb.AppendLine("[TABELA]");
                foreach (var tr in tab.Elements<W.TableRow>())
                    sb.AppendLine(string.Join(" | ", tr.Elements<W.TableCell>().Select(c => c.InnerText.Trim())));
                sb.AppendLine("[FIM DA TABELA]");
            }
        }
        return sb.ToString();
    }

    /// <summary>Cada planilha vira linhas "A; B; C" (valores calculados das fórmulas, como o Excel mostra).</summary>
    static string TextoExcel(byte[] bytes)
    {
        using var doc = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
        var wb = doc.WorkbookPart!;
        var compartilhados = wb.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
        var sb = new StringBuilder();
        foreach (var sheet in wb.Workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            if (wb.GetPartById(sheet.Id!) is not WorksheetPart ws) continue;
            sb.AppendLine($"[PLANILHA: {sheet.Name}]");
            foreach (var row in ws.Worksheet.Descendants<Row>().Take(2000))
            {
                var celulas = row.Elements<Cell>().Select(c =>
                {
                    var v = c.CellValue?.Text ?? c.InlineString?.InnerText ?? "";
                    return c.DataType?.Value == CellValues.SharedString && int.TryParse(v, out var i) && i < compartilhados.Count ? compartilhados[i] : v;
                }).ToList();
                if (celulas.Any(x => x.Length > 0)) sb.AppendLine(string.Join("; ", celulas));
            }
        }
        return sb.ToString();
    }
}
