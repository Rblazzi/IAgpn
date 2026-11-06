using System.Collections.Concurrent;

namespace RelatorioIA.Api.Services;

public class JobChat
{
    public required string Id { get; init; }
    public string Status { get; set; } = "fila";          // fila | processando | concluido | erro
    public string Etapa { get; set; } = "Na fila…";
    public DateTime Inicio { get; } = DateTime.UtcNow;
    public DateTime? Fim { get; set; }
    public ChatResponse? Resultado { get; set; }
    public string? Erro { get; set; }
    internal CancellationTokenSource Cts { get; } = new();
}

public record StatusJob(string Id, string Status, string Etapa, int Segundos, ChatResponse? Resultado, string? Erro);

/// <summary>
/// Executa os pedidos do chat em segundo plano. Motivo: um relatório pode levar minutos (IA + SQL + Word/PDF) e o
/// IIS corta requisições longas (padrão ~2 min). A tela inicia o pedido e consulta o andamento (etapa real).
/// Armazena em memória: funciona com 1 instância do site; para várias instâncias, usar um store compartilhado.
/// </summary>
public class JobsChat(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<JobsChat> log)
{
    readonly ConcurrentDictionary<string, JobChat> jobs = new();
    // Limita pedidos simultâneos à IA (cota/limite de requisições do Gemini)
    readonly SemaphoreSlim vagas = new(cfg.GetValue("Chat:PedidosSimultaneos", 3));
    readonly TimeSpan tempoMaximo = TimeSpan.FromMinutes(cfg.GetValue("Chat:TempoMaximoMinutos", 8));

    public JobChat Iniciar(ChatRequest req)
    {
        Limpar();
        var job = new JobChat { Id = Guid.NewGuid().ToString("N") };
        jobs[job.Id] = job;
        job.Cts.CancelAfter(tempoMaximo);

        _ = Task.Run(async () =>
        {
            try
            {
                await vagas.WaitAsync(job.Cts.Token);
                try
                {
                    job.Status = "processando";
                    using var scope = scopes.CreateScope();
                    var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
                    job.Resultado = await chat.ProcessarAsync(req, job.Cts.Token, e => job.Etapa = e);
                    job.Status = "concluido";
                }
                finally { vagas.Release(); }
            }
            catch (OperationCanceledException)
            {
                job.Status = "erro";
                job.Erro = $"O pedido passou de {tempoMaximo.TotalMinutes:0} minutos e foi cancelado (a IA pode estar sobrecarregada). Tente novamente.";
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Falha no pedido do chat {Id}", job.Id);
                job.Status = "erro";
                job.Erro = "Ocorreu um erro inesperado ao processar o pedido. Tente novamente.";
            }
            finally { job.Fim = DateTime.UtcNow; }
        });
        return job;
    }

    public StatusJob? Obter(string id) =>
        jobs.TryGetValue(id, out var j)
            ? new StatusJob(j.Id, j.Status, j.Etapa, (int)((j.Fim ?? DateTime.UtcNow) - j.Inicio).TotalSeconds, j.Resultado, j.Erro)
            : null;

    public bool Cancelar(string id)
    {
        if (!jobs.TryGetValue(id, out var j) || j.Fim != null) return false;
        j.Cts.Cancel();
        return true;
    }

    /// <summary>Remove pedidos finalizados há mais de 1 hora.</summary>
    void Limpar()
    {
        foreach (var (id, j) in jobs)
            if (j.Fim is DateTime f && DateTime.UtcNow - f > TimeSpan.FromHours(1) && jobs.TryRemove(id, out var r))
                r.Cts.Dispose();
    }
}
