using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Data;
using RelatorioIA.Api.Services;

namespace RelatorioIA.Api.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(ChatService chat, JobsChat jobs) : ControllerBase
{
    static string? Validar(ChatRequest req) =>
        string.IsNullOrWhiteSpace(req.Mensagem) ? "Mensagem vazia." :
        req.Mensagem.Length > 2000 ? "Mensagem muito longa (máx. 2000 caracteres)." :
        req.Historico is { Count: > 50 } ? "Histórico muito longo." :
        req.Anexos is { Count: > Anexos.MaximoPorPedido } ? $"Máximo de {Anexos.MaximoPorPedido} arquivos por pedido." :
        req.Anexos?.Any(a => !Anexos.IdValido(a)) == true ? "Anexo inválido." : null;

    /// <summary>Síncrono (uso em testes/integrações rápidas). Na tela, use iniciar + consultar.</summary>
    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Post([FromBody] ChatRequest req, CancellationToken ct)
    {
        if (Validar(req) is { } erro) return BadRequest(erro);
        return await chat.ProcessarAsync(req, ct);
    }

    /// <summary>Inicia o pedido em segundo plano e devolve o id para acompanhar (evita o timeout do IIS).</summary>
    [HttpPost("iniciar")]
    public IActionResult Iniciar([FromBody] ChatRequest req)
    {
        if (Validar(req) is { } erro) return BadRequest(erro);
        var job = jobs.Iniciar(req);
        return Accepted(new { id = job.Id });
    }

    [HttpGet("{id:regex(^[[a-f0-9]]{{32}}$)}")]
    public ActionResult<StatusJob> Status(string id) => jobs.Obter(id) is { } s ? s : NotFound();

    [HttpDelete("{id:regex(^[[a-f0-9]]{{32}}$)}")]
    public IActionResult Cancelar(string id) => jobs.Cancelar(id) ? NoContent() : NotFound();
}

[ApiController]
[Route("api/relatorios")]
public class RelatoriosController(ChatService chat) : ControllerBase
{
    /// <summary>Download do relatório: ?formato=docx (padrão) ou ?formato=pdf</summary>
    [HttpGet("{id:regex(^[[a-f0-9]]{{12}}$)}")]
    public IActionResult Baixar(string id, [FromQuery] string formato = "docx")
    {
        var (ext, mime) = formato.ToLowerInvariant() switch
        {
            "pdf" => ("pdf", "application/pdf"),
            "docx" => ("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
            _ => (null, null)
        };
        if (ext == null) return BadRequest("Formato inválido (use docx ou pdf).");
        if (!Directory.Exists(chat.PastaRelatorios)) return NotFound();
        var arquivo = Directory.GetFiles(chat.PastaRelatorios, $"{id}__*.{ext}").FirstOrDefault();
        if (arquivo == null) return NotFound();
        var nome = Path.GetFileName(arquivo)[(id.Length + 2)..];
        return PhysicalFile(arquivo, mime!, nome);
    }
}

[ApiController]
[Route("api/anexos")]
public class AnexosController(Anexos anexos) : ControllerBase
{
    /// <summary>Recebe um arquivo do chat (PDF, Word, Excel, CSV, TXT, PNG, JPG — até 10 MB) e devolve o id para o pedido.</summary>
    [HttpPost]
    [RequestSizeLimit(Anexos.TamanhoMaximo + 1024 * 1024)]
    public async Task<ActionResult<AnexoSalvo>> Enviar(IFormFile arquivo, CancellationToken ct)
    {
        if (arquivo is null) return BadRequest("Nenhum arquivo enviado.");
        await using var s = arquivo.OpenReadStream();
        var (anexo, erro) = await anexos.SalvarAsync(arquivo.FileName, s, arquivo.Length, ct);
        return anexo is null ? BadRequest(erro) : anexo;
    }
}

[ApiController]
[Route("api/dados")]
public class DadosController(CalculadoraRelatorio calc) : ControllerBase
{
    /// <summary>Municípios, anos e meses disponíveis no banco do painel (Tcudb), com meses completos/parciais.</summary>
    [HttpGet("catalogo")]
    public async Task<IActionResult> Catalogo(CancellationToken ct) => Ok(await calc.CatalogoAsync(ct));
}
