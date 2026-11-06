using System.Text;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Models;
using RelatorioIA.Api.Services.Consultas;
using RelatorioIA.Api.Services.Documentos;
using RelatorioIA.Api.Services.IA;

namespace RelatorioIA.Api.Services;

/// <summary>Municipio: o município selecionado na tela (como o "selecionar-municipio" do painel). Opcional.</summary>
/// <param name="RelatorioAnterior">Id do último relatório gerado nesta conversa (para alterar/continuar o arquivo anterior).</param>
/// <param name="Anexos">Ids dos arquivos enviados por api/anexos para este pedido.</param>
public record ChatRequest(string Mensagem, List<MensagemChat>? Historico, string? Municipio = null, string? RelatorioAnterior = null,
    List<string>? Anexos = null);
public record ArquivoGerado(string Formato, string Url, long Tamanho);
public record RelatorioGerado(string Id, string NomeArquivo, List<ArquivoGerado> Arquivos);
public record ConsultaExecutada(string Id, string Objetivo, string Sql, int Linhas, string? Erro);
public record ChatResponse(string Resposta, RelatorioGerado? Relatorio, ParametrosRelatorio? Parametros,
    List<ConsultaExecutada>? Consultas, string Motor, string Tipo, List<string>? Sugestoes = null);

/// <summary>
/// Fluxo do chat:
///  1. Prompt 1 (IA) entende o pedido → plano: relatório gerencial | relatório por consulta | resposta | esclarecer
///  2a. Gerencial: números calculados pela <see cref="CalculadoraRelatorio"/> + textos (Prompt 3)
///  2b. Consulta: SQL da IA validado e executado (somente leitura); se falhar, a IA corrige; Prompt 2 monta o relatório
///  3. Documento padrão → Word + PDF para download
/// </summary>
public class ChatService(AssistenteRelatorios ia, CalculadoraRelatorio calc, ExecutorSql sql, EsquemaConsulta esquema,
    MontadorGerencial gerencial, MontadorConsulta consulta, RenderizadorDocx docx, RenderizadorPdf pdf,
    IFonteBrasao brasoes, Anexos anexosSvc, IOptions<RelatorioOptions> opt, IWebHostEnvironment env, ILogger<ChatService> log)
{
    /// <summary>Brasões dos municípios para a capa (um, ou até 3 no comparativo). Falha → capa sem brasão.</summary>
    async Task IncluirBrasoesAsync(Documento doc, IEnumerable<string> municipios, CancellationToken ct)
    {
        foreach (var m in municipios.Distinct().Take(3))
            if (await brasoes.ObterAsync(m, ct) is { } png) doc.Brasoes.Add(png);
    }

    public string PastaRelatorios => Path.GetFullPath(Path.Combine(env.ContentRootPath, opt.Value.Pasta));

    // Estado do pedido em andamento (o serviço é criado por pedido) — usado para montar as sugestões
    List<string> sugestoesIa = [];
    Plano? planoAtual;
    IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogoAtual = [];

    public async Task<ChatResponse> ProcessarAsync(ChatRequest req, CancellationToken ct, Action<string>? etapa = null)
    {
        // Tempo de cada etapa no log (banco, IA, documentos) — para acompanhar o desempenho
        var total = System.Diagnostics.Stopwatch.StartNew();
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        string? atual = null;
        var tempos = new List<string>();
        void Etapa(string e)
        {
            if (atual != null) tempos.Add($"{atual.TrimEnd('…')} {relogio.Elapsed.TotalSeconds:0.0}s");
            atual = e;
            relogio.Restart();
            etapa?.Invoke(e);
        }
        var resposta = await ProcessarInternoAsync(req, ct, Etapa);
        Etapa("fim");
        log.LogInformation("Tempo total {Total:0.0}s ({Tipo}) — {Etapas}", total.Elapsed.TotalSeconds, resposta.Tipo, string.Join(" | ", tempos));
        return resposta with { Sugestoes = SugestoesFinais(req.Mensagem) };
    }

    async Task<ChatResponse> ProcessarInternoAsync(ChatRequest req, CancellationToken ct, Action<string> etapa)
    {
        etapa("Entendendo o pedido…");
        var historico = req.Historico ?? [];
        var catalogo = await calc.CatalogoAsync(ct);
        catalogoAtual = catalogo;
        ia.DefinirCatalogo(catalogo);
        if (catalogo.Count == 0)
            return Resp("Nenhum dado encontrado no banco do painel (Tcudb). Verifique a conexão e as permissões do usuário.", "esclarecer");

        string? valores = null;
        async Task<string> Valores() => valores ??= await esquema.ValoresDistintosAsync(ct);

        var municipios = catalogo.Select(c => c.Municipio).Distinct().ToList();
        // Aceita o nome ("Águas da Prata", com ou sem acento) ou o código do banco ("aguas-da-prata") → nome de exibição
        string? Valido(string? m)
        {
            if (string.IsNullOrWhiteSpace(m)) return null;
            var alvo = SemAcento(m.Trim()).ToLowerInvariant();
            return catalogo.FirstOrDefault(c => SemAcento(c.Municipio).ToLowerInvariant() == alvo
                                                || string.Equals(c.Codigo, m.Trim(), StringComparison.OrdinalIgnoreCase))?.Municipio;
        }
        var selecionado = Valido(req.Municipio);

        // Continuidade: o relatório anterior da conversa (pedido, consultas, seções, anexos) vai para a IA
        var anterior = await CarregarContextoAsync(req.RelatorioAnterior, ct);
        // Anexos deste pedido + os do relatório anterior (para alterar um relatório feito com arquivo, sem reenviar)
        var idsAnexos = (req.Anexos ?? []).Concat(anterior?.Anexos ?? []).Distinct().ToList();
        var anexos = await anexosSvc.CarregarAsync(idsAnexos, ct);
        if (anexos.Count > 0) log.LogInformation("Anexos no pedido: {Anexos}", string.Join(", ", anexos.Select(a => a.Nome)));

        var (plano, modoLocal) = await ia.PlanejarAsync(req.Mensagem, historico, catalogo, selecionado, Valores, ct, anexos, anterior);
        planoAtual = plano;
        // Municípios envolvidos: os citados no pedido (comparativo entre cidades) ou, se nenhum, o padrão da tela
        plano.Municipios = plano.Municipios.Append(plano.Municipio).Select(Valido).OfType<string>().Distinct().ToList();
        if (plano.Municipios.Count == 0 && (selecionado ?? (municipios.Count == 1 ? municipios[0] : null)) is { } padrao)
            plano.Municipios.Add(padrao);
        plano.Municipio = Valido(plano.Municipio) ?? plano.Municipios.FirstOrDefault();
        var motor = modoLocal ? AssistenteLocal.Nome : ia.Nome;
        log.LogInformation("Plano: {Acao} | {Titulo} | {N} consultas | local={Local}", plano.Acao, plano.Titulo, plano.Consultas.Count, modoLocal);

        var avisoLocal = modoLocal && ia.TemIA ? "(A IA não respondeu; pedido interpretado localmente.)" : null;

        // Sem IA, um pedido de projeção vira a consulta padrão do histórico mensal (o cálculo da projeção é do sistema)
        if (modoLocal && PedeProjecao(req.Mensagem) && plano.Municipios.Count > 0)
        {
            ProjecaoLocal(plano, req.Mensagem, catalogo);
            log.LogInformation("Modo local: projeção montada com a consulta padrão ({Titulo})", plano.Titulo);
        }

        switch (plano.Acao)
        {
            case "esclarecer":
                return Resp(plano.Mensagem ?? "Pode detalhar o que você precisa? Ex.: \"Relatório gerencial de janeiro a maio de 2026 comparando com 2025\".", "esclarecer", motor);

            case "alterar_assinatura":
                return await AlterarAssinaturaAsync(req.RelatorioAnterior, plano.Assinatura, motor, ct);

            case "relatorio_consulta" or "responder" when plano.Consultas.Count > 0 && plano.Municipios.Count == 0:
                return Resp($"Para qual município? Disponíveis: {string.Join(", ", municipios)}.", "esclarecer", motor);

            // Com consultas ao banco, ou só sobre o(s) arquivo(s) anexado(s)
            case "relatorio_consulta" or "responder" when plano.Consultas.Count > 0 || anexos.Count > 0:
            {
                var resultados = new List<ResultadoConsulta>();
                if (plano.Consultas.Count > 0)
                {
                    etapa($"Consultando o banco de dados ({plano.Consultas.Count} consulta(s))…");
                    resultados = await ExecutarComCorrecaoAsync(req.Mensagem, plano, catalogo, Valores, ct, etapa);
                }
                TraduzirMunicipios(resultados, catalogo);
                var executadas = resultados.Select(r => new ConsultaExecutada(r.Id, r.Objetivo, r.Sql, r.Linhas.Count, r.Erro)).ToList();
                // Auditoria: o SQL usado fica no log do servidor (não é exibido ao cliente)
                foreach (var c in executadas)
                    log.LogInformation("SQL {Id} ({Linhas} linhas{Erro}) — pedido \"{Pedido}\": {Sql}", c.Id, c.Linhas,
                        c.Erro is null ? "" : ", erro: " + c.Erro, req.Mensagem, c.Sql);
                if (resultados.Count > 0 && resultados.All(r => !r.Ok) && anexos.Count == 0)
                    return Resp("Não consegui consultar o banco para esse pedido (as consultas geradas falharam). " +
                                "Tente reformular, por exemplo indicando o período e o tema.", "erro", motor, executadas);

                // Rede de segurança: pedido de projeção sem o campo "projecao" no plano (modelos "lite" às vezes omitem)
                if (plano.Projecao is null && PedeProjecao(req.Mensagem) &&
                    resultados.FirstOrDefault(r => r.Ok && r.Colunas.Contains("ano") && r.Colunas.Contains("mes") && r.Linhas.Count >= 12) is { } hist)
                {
                    plano.Projecao = new ProjecaoPlanejada
                    {
                        ConsultaId = hist.Id, AteAno = AnoAlvo(req.Mensagem), AteMes = 12,
                        Metrica = string.IsNullOrWhiteSpace(plano.Titulo) ? hist.Objetivo : plano.Titulo
                    };
                    log.LogInformation("Projeção detectada no pedido (o plano da IA não trazia): consulta {Id} até {Ano}", hist.Id, plano.Projecao.AteAno);
                }

                // Projeção: calculada pelo SISTEMA a partir do histórico mensal (a IA só redige)
                string? notaProjecao = null, avisoProjecao = null;
                if (plano.Projecao is { } pj)
                {
                    etapa("Calculando a projeção a partir do histórico…");
                    var fonte = resultados.FirstOrDefault(r => r.Ok && string.Equals(r.Id, pj.ConsultaId, StringComparison.OrdinalIgnoreCase))
                                ?? resultados.FirstOrDefault(r => r.Ok && r.Colunas.Contains("ano") && r.Colunas.Contains("mes"));
                    var serieMensal = fonte is null ? null : Projetor.LerHistorico(fonte, pj.CampoValor);
                    // Meses incompletos (algum órgão ainda não enviou ao TCESP) NÃO contam como realizados
                    if (serieMensal != null)
                        serieMensal = serieMensal.Where(p => catalogoAtual
                            .Where(c => c.Ano == p.Ano && plano.Municipios.Contains(c.Municipio))
                            .All(c => p.Mes <= Math.Min(c.MesMaxReceita ?? 12, c.MesMaxDespesa ?? 12))).ToList();
                    var proj = serieMensal is null ? null : Projetor.Projetar(serieMensal, pj.AteAno,
                        pj.AteMes is >= 1 and <= 12 ? pj.AteMes : 12, DateOnly.FromDateTime(DateTime.Today));
                    if (proj != null)
                    {
                        var (mensal, anual, estatistica) = Projetor.ComoResultados(proj, string.IsNullOrWhiteSpace(pj.Metrica) ? "Valor" : pj.Metrica);
                        resultados.Add(mensal);
                        resultados.Add(anual);
                        if (estatistica.Linhas.Count > 0) resultados.Add(estatistica);
                        notaProjecao = proj.Metodo;
                        log.LogInformation("Projeção calculada: taxa {Taxa:P2}, até {Mes}/{Ano}", proj.TaxaCrescimento, pj.AteMes, pj.AteAno);
                    }
                    else avisoProjecao = "Não foi possível calcular a projeção: é necessário o histórico mensal com ao menos um ano " +
                                         "completo anterior ao período mais recente, e o horizonte deve ser posterior ao último mês com dados.";
                }

                if (plano.Acao == "responder")
                {
                    etapa("Escrevendo a resposta…");
                    var (respostaIa, sugs) = await ia.ResponderAsync(req.Mensagem, historico, resultados, ct, anexos);
                    sugestoesIa = sugs;
                    var texto = respostaIa ?? ListarResultados(resultados);
                    if (avisoProjecao != null) texto += "\n⚠ " + avisoProjecao;
                    else if (notaProjecao != null) texto += "\n\n" + notaProjecao;
                    return Resp(texto, "resposta", motor, executadas);
                }

                // Capa: um município, ou "A × B" no comparativo entre cidades
                var municipio = string.Join(" × ", plano.Municipios);
                etapa("Redigindo o relatório…");
                var estrutura = await ia.RedigirConsultaAsync(req.Mensagem, plano, resultados, ct, notaProjecao, anexos, anterior)
                                ?? EstruturaPadrao(plano, resultados);
                sugestoesIa = estrutura.Sugestoes;
                var avisos = resultados.Where(r => !r.Ok).Select(r => $"A consulta \"{r.Objetivo}\" não pôde ser executada.").ToList();
                if (avisoProjecao != null) avisos.Add(avisoProjecao);
                // Nota de método da projeção: curta, na conclusão (pedido da gerência — sem destaque no início do relatório)
                var (doc, naoConferidos) = consulta.Montar(estrutura, plano, resultados, municipio, avisos, notaProjecao, anexos);
                if (naoConferidos.Count > 0)
                    log.LogWarning("Valores citados pela IA não encontrados nos resultados: {Valores}", string.Join(", ", naoConferidos));

                etapa("Gerando os arquivos Word e PDF…");
                await IncluirBrasoesAsync(doc, plano.Municipios, ct);
                // Nome do arquivo: "Comparativo <título>" sem repetir a palavra quando o título já começa com ela
                var prefixo = plano.Municipios.Count > 1 ? "Comparativo" : municipio;
                var nomeArquivo = estrutura.Titulo.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)
                    ? estrutura.Titulo : $"{prefixo} {estrutura.Titulo}";
                var rel = await SalvarAsync(doc, nomeArquivo, ct);
                await SalvarContextoAsync(rel, new ContextoRelatorio
                {
                    Pedido = req.Mensagem, Tipo = "consulta", Titulo = estrutura.Titulo, Plano = plano, Estrutura = estrutura,
                    Anexos = anexos.Select(a => a.Id).ToList()
                }, ct);
                var sb = new StringBuilder($"Relatório gerado: **{estrutura.Titulo}**");
                if (!string.IsNullOrWhiteSpace(plano.PeriodoDescricao)) sb.Append($" ({plano.PeriodoDescricao})");
                sb.AppendLine(".");
                sb.AppendLine($"• {Plural(estrutura.Secoes.Count, "seção", "seções")} de análise, {Plural(doc.Blocos.OfType<TabelaDoc>().Count(), "tabela", "tabelas")} " +
                              $"e {Plural(doc.Blocos.OfType<GraficoDoc>().Count(), "gráfico", "gráficos")}.");
                if (naoConferidos.Count > 0)
                    sb.AppendLine($"⚠ Confira no documento estes valores, que não aparecem diretamente nas consultas nem no arquivo anexado: {string.Join("; ", naoConferidos.Select(v => "R$ " + v))}.");
                if (anexos.Count > 0) sb.AppendLine($"• Arquivo(s) considerado(s): {string.Join(", ", anexos.Select(a => a.Nome))}");
                if (plano.Assinatura is { } quem) sb.AppendLine($"• Assinatura na última página: {quem}");
                if (plano.Mensagem is { Length: > 0 } m) sb.AppendLine(m);
                return Resp(sb.ToString().TrimEnd(), "relatorio", motor, executadas, rel);
            }

            case "relatorio_consulta" when !modoLocal:
                // A IA pediu um relatório de tema específico mas não conseguiu montar o SQL: não troca por outro relatório
                return Resp("Não consegui montar a consulta para esse tema. Tente detalhar um pouco mais (por exemplo: o período, " +
                            "a área — saúde, educação… — e o que deseja ver: ranking, evolução mensal, comparação).", "erro", motor);

            default: // relatorio_gerencial, ou responder sem SQL (modo local)
            {
                var (parametros, observacoes, erro) = Completar(plano, catalogo);
                if (erro != null) return Resp(erro, "esclarecer", motor);

                etapa("Calculando receitas e despesas no banco…");
                var dados = await calc.CalcularAsync(parametros!, ct);
                dados.Avisos.InsertRange(0, observacoes);

                if (plano.Acao == "responder")
                    return Resp(ia.ResponderResumoLocal(dados), "resposta", motor) with { Parametros = parametros };

                etapa("Redigindo as análises do relatório…");
                var textos = await ia.RedigirGerencialAsync(dados, req.Mensagem, ct);
                sugestoesIa = textos.Sugestoes;
                etapa("Gerando os arquivos Word e PDF…");
                var swMontar = System.Diagnostics.Stopwatch.StartNew();
                var doc = gerencial.Montar(dados, textos, plano.Assinatura);
                log.LogInformation("Documento montado (com {N} gráficos) em {S:0.00}s", doc.Blocos.OfType<GraficoDoc>().Count(), swMontar.Elapsed.TotalSeconds);
                await IncluirBrasoesAsync(doc, [parametros!.Municipio], ct);
                var rel = await SalvarAsync(doc, $"{parametros.Municipio} Relatório Gerencial", ct);
                await SalvarContextoAsync(rel, new ContextoRelatorio
                {
                    Pedido = req.Mensagem, Tipo = "gerencial", Titulo = "Relatório Gerencial", Parametros = parametros
                }, ct);
                var resumo = ResumoGerencial(dados, plano.Mensagem, avisoLocal) +
                             $"\n• Documento com {Plural(doc.Blocos.OfType<TabelaDoc>().Count(), "tabela", "tabelas")} e " +
                             $"{Plural(doc.Blocos.OfType<GraficoDoc>().Count(), "gráfico", "gráficos")}.";
                if (plano.Assinatura is { } quem) resumo += $"\n• Assinatura na última página: {quem}";
                return Resp(resumo, "relatorio", motor, null, rel) with { Parametros = parametros };
            }
        }
    }

    /// <summary>Executa as consultas; as que falharem voltam para a IA corrigir (até 2 rodadas).</summary>
    async Task<List<ResultadoConsulta>> ExecutarComCorrecaoAsync(string pedido, Plano plano,
        IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogo, Func<Task<string>> valores, CancellationToken ct,
        Action<string> etapa)
    {
        var resultados = new List<ResultadoConsulta>();
        // Regras do sistema conferidas ANTES de executar (não dependem da IA): consulta reprovada volta para a IA corrigir
        async Task<ResultadoConsulta> Executar(string id, string objetivo, string sqlIa)
        {
            if (RegrasConsulta.Problema(sqlIa, catalogo) is { } problema)
            {
                log.LogWarning("Consulta {Id} reprovada pelas regras do sistema: {Problema}", id, problema);
                return new ResultadoConsulta { Id = id, Objetivo = objetivo, Sql = sqlIa, Erro = problema };
            }
            return await sql.ExecutarAsync(id, objetivo, sqlIa, ct);
        }
        // Em paralelo (cada consulta abre a própria conexão); a ordem do plano é mantida
        var consultas = plano.Consultas.Take(6).ToList();
        resultados.AddRange(await Task.WhenAll(consultas.Select((c, i) => Executar(Id(c, i), c.Objetivo, c.Sql))));

        // Consulta que volta vazia costuma ser filtro errado: devolve para a IA revisar (1 rodada)
        bool Vazia(ResultadoConsulta r) => r.Ok && r.Linhas.Count == 0;
        var revisarVazias = true;

        for (int rodada = 1; rodada <= 2 && resultados.Any(r => !r.Ok || (revisarVazias && Vazia(r))); rodada++)
        {
            var falhas = resultados.Where(r => !r.Ok || (revisarVazias && Vazia(r)))
                .Select(r => r.Ok
                    ? new ResultadoConsulta
                    {
                        Id = r.Id, Objetivo = r.Objetivo, Sql = r.Sql,
                        Erro = "A consulta executou mas retornou 0 linhas. Revise os filtros: use os valores exatos da lista de " +
                               "valores distintos, LIKE com termos mais amplos (ex.: '%PARTICIPACAO DOS MUNICIPIOS%', '%PATRIMONIO%') " +
                               "e confira em qual coluna o termo aparece (rubrica, alinea, subalinea, detalhamento, elemento_despesa)."
                    }
                    : r).ToList();
            revisarVazias = false;
            etapa($"Revisando {falhas.Count} consulta(s) com a IA (erro ou resultado vazio)…");
            log.LogWarning("Rodada {R}: {N} consulta(s) com erro — pedindo correção à IA: {Erros}", rodada, falhas.Count,
                string.Join(" | ", falhas.Select(f => f.Erro)));
            var corrigido = await ia.CorrigirPlanoAsync(pedido, plano, falhas, catalogo, valores, ct);
            if (corrigido == null) break;
            var refeitas = await Task.WhenAll(falhas.Select(async f =>
            {
                var nova = corrigido.Consultas.FirstOrDefault(c => string.Equals(c.Id, f.Id, StringComparison.OrdinalIgnoreCase));
                return (f.Id, novo: nova == null ? null : await Executar(f.Id, nova.Objetivo, nova.Sql));
            }));
            foreach (var (id, novo) in refeitas)
            {
                var idx = resultados.FindIndex(r => r.Id == id);
                // Só troca se a correção não piorou (ex.: vazia → erro)
                if (novo != null && idx >= 0 && (novo.Ok || !resultados[idx].Ok)) resultados[idx] = novo;
            }
            plano.Consultas = corrigido.Consultas;
        }
        return resultados;
    }

    static string Plural(int n, string um, string varios) => $"{n} {(n == 1 ? um : varios)}";

    static string Id(ConsultaPlanejada c, int i) => string.IsNullOrWhiteSpace(c.Id) ? $"c{i + 1}" : c.Id;

    /// <summary>Sem IA para redigir: cada consulta vira uma seção com sua tabela.</summary>
    static EstruturaRelatorio EstruturaPadrao(Plano plano, IReadOnlyList<ResultadoConsulta> resultados) => new()
    {
        Titulo = plano.Titulo ?? "Análise",
        Subtitulo = plano.Titulo ?? "",
        Introducao = $"Este relatório apresenta os dados do Tribunal de Contas do Estado de São Paulo (TCESP) referentes a {plano.PeriodoDescricao ?? "o período solicitado"}.",
        Secoes = resultados.Where(r => r.Ok).Select(r => new SecaoRelatorio
        {
            Titulo = r.Objetivo,
            Tabela = new TabelaPlanejada { ConsultaId = r.Id, Titulo = r.Objetivo }
        }).ToList(),
        Conclusao = "Recomenda-se a análise qualitativa dos dados apresentados em conjunto com os gestores responsáveis."
    };

    static string ListarResultados(IEnumerable<ResultadoConsulta> resultados)
    {
        var sb = new StringBuilder();
        foreach (var r in resultados.Where(r => r.Ok))
        {
            sb.AppendLine($"**{r.Objetivo}**");
            foreach (var l in r.Linhas.Take(10))
                sb.AppendLine("• " + string.Join(" · ", l.Select(v => ExecutorSql.Numerico(v) ? Fmt.Moeda(ExecutorSql.ParaDecimal(v)) : v?.ToString())));
        }
        return sb.ToString().TrimEnd();
    }

    static readonly System.Text.Json.JsonSerializerOptions JsonDoc = new() { IncludeFields = false };

    /// <summary>
    /// Troca só a assinatura do último relatório da conversa: reabre o documento salvo (JSON), troca a linha e gera
    /// Word/PDF de novo. Mesmo conteúdo, sem consultar o banco nem a IA.
    /// </summary>
    async Task<ChatResponse> AlterarAssinaturaAsync(string? relatorioId, string? assinatura, string motor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assinatura))
            return Resp("Qual texto deve aparecer na assinatura da última página? Ex.: \"Gestor responsável\".", "esclarecer", motor);
        var json = relatorioId is { Length: 12 } && relatorioId.All(Uri.IsHexDigit) && Directory.Exists(PastaRelatorios)
            ? Directory.GetFiles(PastaRelatorios, $"{relatorioId}__*.json").FirstOrDefault() : null;
        if (json == null)
            return Resp("Não encontrei o relatório anterior desta conversa para alterar. Gere o relatório novamente informando " +
                        $"a assinatura, por exemplo: \"... assinado por {assinatura}\".", "esclarecer", motor);

        var doc = System.Text.Json.JsonSerializer.Deserialize<Documento>(await File.ReadAllTextAsync(json, ct), JsonDoc)!;
        doc.TrocarAssinatura(assinatura);
        var nomeAnterior = System.Text.RegularExpressions.Regex.Replace(
            Path.GetFileNameWithoutExtension(json)[(relatorioId!.Length + 2)..], @" - \d{2}-\d{2}-\d{4}$", "");
        var rel = await SalvarAsync(doc, nomeAnterior, ct);
        // O novo arquivo herda o contexto do anterior (a conversa continua podendo alterá-lo)
        if (await CarregarContextoAsync(relatorioId, ct) is { } ctxAnterior) await SalvarContextoAsync(rel, ctxAnterior, ct);
        return Resp($"Relatório atualizado: **{nomeAnterior}**.\n• Assinatura na última página: {assinatura}", "relatorio", motor, null, rel);
    }

    /// <summary>Contexto do relatório (pedido, plano, estrutura, anexos) ao lado dos arquivos: {id}__{nome}.contexto</summary>
    async Task SalvarContextoAsync(RelatorioGerado rel, ContextoRelatorio c, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(Path.Combine(PastaRelatorios, $"{rel.Id}__{rel.NomeArquivo}.contexto"),
                System.Text.Json.JsonSerializer.Serialize(c, JsonIA.Opcoes), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Não foi possível salvar o contexto do relatório"); }
    }

    async Task<ContextoRelatorio?> CarregarContextoAsync(string? relatorioId, CancellationToken ct)
    {
        if (relatorioId is not { Length: 12 } || !relatorioId.All(Uri.IsHexDigit) || !Directory.Exists(PastaRelatorios)) return null;
        var arq = Directory.GetFiles(PastaRelatorios, $"{relatorioId}__*.contexto").FirstOrDefault();
        if (arq is null) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<ContextoRelatorio>(await File.ReadAllTextAsync(arq, ct), JsonIA.Opcoes); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Contexto do relatório {Id} ilegível", relatorioId); return null; }
    }

    async Task<RelatorioGerado> SalvarAsync(Documento doc, string nomeBase, CancellationToken ct)
    {
        Directory.CreateDirectory(PastaRelatorios);
        var id = Guid.NewGuid().ToString("N")[..12];
        var invalidos = Path.GetInvalidFileNameChars();
        // "Título: subtítulo" → "Título - subtítulo" (dois-pontos não vale em nome de arquivo)
        nomeBase = System.Text.RegularExpressions.Regex.Replace(nomeBase, @"\s*:\s*", " - ").Trim();
        var nome = new string($"{nomeBase} - {DateTime.Now:dd-MM-yyyy}".Select(c => invalidos.Contains(c) ? '-' : c).ToArray());
        if (nome.Length > 120) nome = nome[..120];

        var arquivos = new List<ArquivoGerado>();
        // Word e PDF ao mesmo tempo (só leem o documento)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double sPdf = 0;
        var tarefaPdf = Task.Run(() => { var t = System.Diagnostics.Stopwatch.StartNew(); var b = pdf.Renderizar(doc); sPdf = t.Elapsed.TotalSeconds; return b; }, ct);
        var bDocx = docx.Renderizar(doc);
        var sDocx = sw.Elapsed.TotalSeconds;
        await File.WriteAllBytesAsync(Path.Combine(PastaRelatorios, $"{id}__{nome}.docx"), bDocx, ct);
        arquivos.Add(new("docx", $"api/relatorios/{id}?formato=docx", bDocx.Length));
        try
        {
            var bPdf = await tarefaPdf;
            await File.WriteAllBytesAsync(Path.Combine(PastaRelatorios, $"{id}__{nome}.pdf"), bPdf, ct);
            arquivos.Add(new("pdf", $"api/relatorios/{id}?formato=pdf", bPdf.Length));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Falha ao gerar o PDF; apenas o Word foi gerado"); }
        // Documento em JSON (não é baixado): permite alterar depois sem refazer (ex.: trocar a assinatura)
        await File.WriteAllTextAsync(Path.Combine(PastaRelatorios, $"{id}__{nome}.json"), System.Text.Json.JsonSerializer.Serialize(doc, JsonDoc), ct);
        log.LogInformation("Arquivos gerados em {Total:0.00}s (Word {Docx:0.00}s, PDF {Pdf:0.00}s, em paralelo)", sw.Elapsed.TotalSeconds, sDocx, sPdf);
        return new RelatorioGerado(id, nome, arquivos);
    }

    /// <summary>Coluna "municipio" dos resultados vem com o código do banco ("aguas-da-prata") → nome ("Águas da Prata").</summary>
    static void TraduzirMunicipios(List<ResultadoConsulta> resultados, IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogo)
    {
        var nomes = catalogo.GroupBy(c => c.Codigo, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Municipio, StringComparer.OrdinalIgnoreCase);
        foreach (var r in resultados.Where(r => r.Ok))
        {
            var i = r.Colunas.FindIndex(c => string.Equals(c, "municipio", StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            foreach (var l in r.Linhas)
                if (l[i] is string cod && nomes.TryGetValue(cod, out var nome)) l[i] = nome;
        }
    }

    static string SemAcento(string s) => new(s.Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());

    /// <summary>O pedido fala de projeção/previsão/estimativa futura?</summary>
    public static bool PedeProjecao(string pedido) =>
        System.Text.RegularExpressions.Regex.IsMatch(SemAcento(pedido.ToLowerInvariant()),
            @"projec|projet|previs|estimativ|tendencia|ate o (final|fim) d|fechar o ano|vai gastar|vamos gastar|vai arrecadar|futur");

    /// <summary>
    /// Plano de projeção sem IA: histórico mensal dos últimos 5 anos da métrica pedida (receita arrecadada, ou despesa
    /// paga — total ou da função de governo citada), projetado até o fim do ano-alvo.
    /// </summary>
    public static void ProjecaoLocal(Plano plano, string pedido, IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogo)
    {
        var p = SemAcento(pedido.ToLowerInvariant());
        var codigos = plano.Municipios.Select(m => catalogo.First(c => c.Municipio == m).Codigo).Distinct()
            .Select(c => "'" + c.Replace("'", "") + "'");
        var desde = DateTime.Today.Year - 4;
        string[][] funcoes =
        [
            ["saude", "SAÚDE"], ["educac", "EDUCAÇÃO"], ["assistencia social", "ASSISTÊNCIA SOCIAL"],
            ["seguranca", "SEGURANÇA PÚBLICA"], ["urbanismo", "URBANISMO"], ["saneamento", "SANEAMENTO"],
            ["previdencia", "PREVIDÊNCIA SOCIAL"], ["cultura", "CULTURA"], ["transporte", "TRANSPORTE"]
        ];
        var funcao = funcoes.FirstOrDefault(f => p.Contains(f[0]))?[1];
        var receita = funcao == null && System.Text.RegularExpressions.Regex.IsMatch(p, @"receita|arrecad");

        string sqlHist, metrica;
        if (receita)
        {
            sqlHist = $"SELECT ano, mes, ROUND(SUM(valor),2) AS valor FROM vw_receitas WHERE municipio IN ({string.Join(",", codigos)}) " +
                      $"AND ano >= {desde} GROUP BY ano, mes ORDER BY ano, mes";
            metrica = "Receita arrecadada";
        }
        else
        {
            sqlHist = $"SELECT ano, mes, ROUND(SUM(valor_pago),2) AS valor FROM vw_despesas WHERE municipio IN ({string.Join(",", codigos)}) " +
                      $"AND ano >= {desde}{(funcao is null ? "" : $" AND funcao = '{funcao}'")} GROUP BY ano, mes ORDER BY ano, mes";
            metrica = funcao is null ? "Despesa paga" : $"Despesa paga com {Fmt.NomeProprio(funcao).ToLowerInvariant()}";
        }
        var ate = AnoAlvo(pedido);
        plano.Acao = "relatorio_consulta";
        plano.Titulo = $"Projeção — {metrica} até dezembro de {ate}";
        plano.PeriodoDescricao = $"histórico desde {desde} e projeção até dez/{ate}";
        plano.Consultas = [new ConsultaPlanejada { Id = "historico", Objetivo = $"{metrica} mensal (histórico)", Sql = sqlHist }];
        plano.Projecao = new ProjecaoPlanejada { ConsultaId = "historico", AteAno = ate, AteMes = 12, Metrica = metrica, CampoValor = "valor" };
    }

    /// <summary>Ano-alvo citado no pedido (maior ano a partir do atual); senão, o ano atual.</summary>
    public static int AnoAlvo(string pedido)
    {
        var atual = DateTime.Today.Year;
        var anos = System.Text.RegularExpressions.Regex.Matches(pedido, @"\b20\d{2}\b").Select(m => int.Parse(m.Value)).Where(a => a >= atual).ToList();
        return anos.Count > 0 ? anos.Max() : atual;
    }

    /// <summary>
    /// 3 sugestões de próximas perguntas: as da IA (limpas e validadas) completadas, se preciso, por sugestões
    /// padrão montadas com o município/ano do pedido e o que existe no banco.
    /// </summary>
    List<string> SugestoesFinais(string pedido)
    {
        static string Limpar(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"^\s*(\d+[\.\)]|[-•*])\s*", "").Trim().Trim('"', '“', '”', '\'');

        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pedido.Trim() };
        var lista = sugestoesIa.Select(Limpar)
            .Where(s => s.Length is >= 10 and <= 140 && vistos.Add(s))
            .Take(3).ToList();
        foreach (var s in SugestoesPadrao())
        {
            if (lista.Count >= 3) break;
            if (vistos.Add(s)) lista.Add(s);
        }
        return lista;
    }

    IEnumerable<string> SugestoesPadrao()
    {
        if (catalogoAtual.Count == 0) yield break;
        var municipios = catalogoAtual.Select(c => c.Municipio).Distinct().ToList();
        var mun = planoAtual?.Municipios.FirstOrDefault() ?? planoAtual?.Municipio ?? municipios[0];
        var anos = catalogoAtual.Where(c => c.Municipio == mun).Select(c => c.Ano).OrderBy(a => a).ToList();
        if (anos.Count == 0) yield break;
        var ano = planoAtual?.Ano is int a && anos.Contains(a) ? a : anos[^1];
        var outro = municipios.FirstOrDefault(m => m != mun);

        yield return $"Quais os 10 maiores fornecedores de {mun} em {ano}?";
        if (anos.Contains(ano - 1)) yield return $"Relatório gerencial de {mun} de {ano} comparando com {ano - 1}";
        if (outro != null) yield return $"Compare a receita e a despesa de {mun} e {outro} em {ano}";
        yield return $"Como evoluiu a despesa paga com saúde e educação mês a mês em {mun} em {ano}?";
        yield return $"Qual foi a arrecadação de ISS e IPTU de {mun} em {ano}?";
    }

    static ChatResponse Resp(string texto, string tipo, string motor = "", List<ConsultaExecutada>? consultas = null, RelatorioGerado? rel = null) =>
        new(texto, rel, null, consultas, motor, tipo);

    /// <summary>Completa campos ausentes com padrões e valida tudo contra o que existe no banco.</summary>
    static (ParametrosRelatorio? p, List<string> obs, string? erro) Completar(Plano i, IReadOnlyList<CalculadoraRelatorio.Disponibilidade> cat)
    {
        var obs = new List<string>();
        var municipios = cat.Select(c => c.Municipio).Distinct().ToList();

        var municipio = municipios.FirstOrDefault(m => string.Equals(m, i.Municipio?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (municipio == null)
        {
            if (municipios.Count == 1) municipio = municipios[0];
            else return (null, obs, $"Para qual município? Disponíveis: {string.Join(", ", municipios)}.");
        }

        var anos = cat.Where(c => c.Municipio == municipio).ToList();
        var ano = i.Ano ?? anos.Max(a => a.Ano);
        var disp = anos.FirstOrDefault(a => a.Ano == ano);
        if (disp == null)
            return (null, obs, $"Não há dados de {ano} para {municipio}. Exercícios disponíveis: {string.Join(", ", anos.Select(a => a.Ano))}.");

        var incRec = i.IncluirReceitas ?? true;
        var incDes = i.IncluirDespesas ?? true;
        if (!incRec && !incDes) incRec = incDes = true;

        var limites = new List<int>();
        if (incRec && disp.MesMaxReceita is int mr) limites.Add(mr);
        if (incDes && disp.MesMaxDespesa is int md) limites.Add(md);
        var mesMax = limites.Count > 0 ? limites.Min() : 12;

        var ini = Math.Clamp(i.MesInicial ?? 1, 1, 12);
        var fim = Math.Clamp(i.MesFinal ?? mesMax, 1, 12);
        if (ini > fim) (ini, fim) = (fim, ini);
        if (fim > mesMax)
        {
            obs.Add($"Os dados completos de {ano} vão até {Fmt.MesTitulo(mesMax)}; o período foi ajustado para terminar nesse mês." +
                    (disp.Pendencias is null ? "" : $" Meses seguintes estão incompletos no TCESP ({disp.Pendencias})."));
            fim = mesMax;
            if (ini > fim) ini = 1;
        }

        int? comp = i.AnoComparacao;
        if (comp == ano) comp = null;
        if (comp is int c && anos.All(a => a.Ano != c))
        {
            obs.Add($"Não há dados de {c} para o comparativo; o relatório foi gerado sem essa seção.");
            comp = null;
        }
        // O ano base precisa ter os mesmos meses do período; senão a comparação fica distorcida
        if (comp is int cb && anos.First(a => a.Ano == cb) is var dBase &&
            Math.Min(dBase.MesMaxReceita ?? 0, dBase.MesMaxDespesa ?? 0) < fim)
        {
            obs.Add($"Os dados de {cb} vão só até {Fmt.MesTitulo(Math.Max(1, Math.Min(dBase.MesMaxReceita ?? 1, dBase.MesMaxDespesa ?? 1)))}; " +
                    "o comparativo foi omitido para não comparar períodos diferentes.");
            comp = null;
        }
        if (comp != null && !(incRec && incDes))
        {
            obs.Add("O comparativo entre anos exige receitas e despesas; as duas seções foram incluídas.");
            incRec = incDes = true;
        }

        return (new ParametrosRelatorio(municipio, ano, ini, fim, comp, incRec, incDes), obs, null);
    }

    static string ResumoGerencial(DadosRelatorio d, string? msgIa, string? avisoLocal)
    {
        var p = d.Parametros;
        var sb = new StringBuilder();
        sb.Append($"Relatório gerencial gerado: **{p.Municipio}**, {Fmt.PeriodoExtenso(p.MesInicial, p.MesFinal).ToLower(Fmt.PtBr)} de {p.Ano}");
        if (d.Comparativo != null) sb.Append($", com comparativo de {d.Comparativo.AnoBase}");
        sb.AppendLine(".");
        if (d.Receita is { } r) sb.AppendLine($"• Receita consolidada: {Fmt.Moeda(r.Total)}");
        if (d.Despesa is { } s) sb.AppendLine($"• Despesa empenhada consolidada: {Fmt.Moeda(s.Consolidada)} (pago: {Fmt.Moeda(s.Pago)})");
        if (d.Receita is { } r2 && d.Despesa is { } s2) sb.AppendLine($"• Resultado (receita − despesa): {Fmt.Moeda(r2.Total - s2.Consolidada)}");
        foreach (var a in d.Avisos) sb.AppendLine($"⚠ {a}");
        if (!string.IsNullOrWhiteSpace(msgIa)) sb.AppendLine(msgIa);
        if (avisoLocal != null) sb.AppendLine(avisoLocal);
        return sb.ToString().TrimEnd();
    }
}
