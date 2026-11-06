using System.Text.Json;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Models;
using RelatorioIA.Api.Services.Consultas;
using RelatorioIA.Api.Services.Documentos;

namespace RelatorioIA.Api.Services.IA;

/// <summary>
/// Orquestra os prompts padrão (pasta Prompts/) contra o modelo de IA.
/// Sem IA configurada (ou com a IA fora do ar), cai para o <see cref="AssistenteLocal"/>.
/// </summary>
public class AssistenteRelatorios(IEnumerable<IModeloIA> modelos, AssistenteLocal local, PromptStore prompts,
    IOptions<RelatorioOptions> relOpt, ILogger<AssistenteRelatorios> log)
{
    readonly IModeloIA? ia = modelos.FirstOrDefault();
    public bool TemIA => ia != null;
    public string Nome => ia?.Nome ?? AssistenteLocal.Nome;

    string catalogoTexto = "";

    /// <summary>Municípios/anos/meses do banco — usados nas regras das sugestões de próximas perguntas.</summary>
    public void DefinirCatalogo(IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogo) =>
        catalogoTexto = TextoCatalogo(catalogo);

    /// <summary>
    /// Catálogo resumido por município (o banco tem muitos exercícios): anos completos agrupados; o detalhe só para
    /// os anos incompletos. Inclui o CÓDIGO usado na coluna municipio do SQL.
    /// </summary>
    static string TextoCatalogo(IEnumerable<CalculadoraRelatorio.Disponibilidade> catalogo) =>
        string.Join("\n", catalogo.GroupBy(c => (c.Municipio, c.Codigo)).Select(g =>
        {
            var completos = g.Where(c => c.MesMaxReceita == 12 && c.MesMaxDespesa == 12).Select(c => c.Ano).OrderBy(a => a).ToList();
            var linhas = new List<string>
            {
                $"- {g.Key.Municipio} — no SQL use municipio = '{g.Key.Codigo}'" +
                (completos.Count > 0 ? $"; exercícios completos (jan a dez): {completos.Min()} a {completos.Max()}" : "")
            };
            foreach (var c in g.Where(c => !completos.Contains(c.Ano)).OrderBy(c => c.Ano))
                linhas.Add($"    • {c.Ano}: receitas COMPLETAS até o mês {c.MesMaxReceita?.ToString() ?? "-"}, despesas COMPLETAS até o mês {c.MesMaxDespesa?.ToString() ?? "-"}" +
                           (c.Pendencias is null ? "" : $" (ATENÇÃO: {c.Pendencias} — não use esses meses sem avisar que estão incompletos)"));
            return string.Join("\n", linhas);
        }));

    Dictionary<string, string> Base()
    {
        var v = new Dictionary<string, string>
        {
            ["CONSULTORIA"] = relOpt.Value.Consultoria,
            ["DATA_HOJE"] = DateTime.Now.ToString("dd/MM/yyyy")
        };
        v["REGRAS_SUGESTOES"] = prompts.Montar(PromptStore.Sugestoes, new Dictionary<string, string> { ["CATALOGO"] = catalogoTexto });
        // Regras de postura comuns a todos os prompts (foco, sem invenção, fora do escopo, alterar só o pedido)
        v["IDENTIDADE"] = prompts.Montar(PromptStore.Identidade, new Dictionary<string, string> { ["CONSULTORIA"] = relOpt.Value.Consultoria });
        return v;
    }

    // ------------------------------------------------------------ Prompt 1: planejar

    /// <param name="anexos">Arquivos enviados com o pedido (a IA lê o conteúdo).</param>
    /// <param name="anterior">Contexto do último relatório da conversa (para alterar ou continuar o arquivo anterior).</param>
    public async Task<(Plano plano, bool local)> PlanejarAsync(string mensagem, IReadOnlyList<MensagemChat> historico,
        IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogo, string? municipioSelecionado,
        Func<Task<string>> valoresDistintos, CancellationToken ct,
        IReadOnlyList<AnexoConteudo>? anexos = null, ContextoRelatorio? anterior = null)
    {
        if (ia == null) return (local.Planejar(mensagem, catalogo), true);
        try
        {
            var sistema = await SistemaPlanejarAsync(catalogo, valoresDistintos, municipioSelecionado) + DescreverAnterior(anterior);
            var conversa = historico.TakeLast(12).Select(h => (h.Papel, h.Texto)).Append(("user", mensagem));
            var json = await ia.GerarJsonAsync(sistema, conversa, SchemaPlano, 0.1, ct, anexos);
            log.LogDebug("Plano bruto da IA: {Json}", Truncar(json.GetRawText(), 2500));
            var plano = json.Deserialize<Plano>(JsonIA.Opcoes) ?? new Plano();

            // Plano que exige SQL mas veio sem consultas (acontece com modelos "lite"): pede de novo, explicitamente
            // (com anexo, um relatório/resposta só sobre o arquivo não precisa de consultas)
            if (plano.Acao is "relatorio_consulta" or "responder" && plano.Consultas.Count == 0 && anexos is not { Count: > 0 })
            {
                log.LogWarning("Plano {Acao} veio sem consultas; pedindo novamente à IA", plano.Acao);
                // Segunda chamada com formato mínimo: só a lista de consultas
                var pedido = $"Pedido do usuário: {mensagem}\nTema: {plano.Titulo}\nPeríodo: {plano.PeriodoDescricao}\n\n" +
                             "Escreva AGORA somente as consultas SQL (1 a 4 SELECTs nas views vw_despesas/vw_receitas) " +
                             "que trazem os dados para atender este pedido.";
                var json2 = await ia.GerarJsonAsync(sistema, [("user", pedido)], SchemaSoConsultas, 0.1, ct, anexos);
                log.LogDebug("Consultas (2ª chamada): {Json}", Truncar(json2.GetRawText(), 2500));
                plano.Consultas = json2.Deserialize<Plano>(JsonIA.Opcoes)?.Consultas ?? [];
            }
            return (Limpar(plano), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "IA falhou ao planejar; usando modo local");
            return (local.Planejar(mensagem, catalogo), true);
        }
    }

    /// <summary>Devolve à IA o plano anterior + os erros das consultas para ela corrigir o SQL.</summary>
    public async Task<Plano?> CorrigirPlanoAsync(string mensagem, Plano anterior, IEnumerable<ResultadoConsulta> falhas,
        IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogo, Func<Task<string>> valoresDistintos, CancellationToken ct)
    {
        if (ia == null) return null;
        try
        {
            var sistema = await SistemaPlanejarAsync(catalogo, valoresDistintos, anterior.Municipio);
            var erros = string.Join("\n", falhas.Select(f => $"- Consulta {f.Id}: {f.Erro}\n  SQL: {f.Sql}"));
            var conversa = new[]
            {
                ("user", mensagem),
                ("assistant", JsonSerializer.Serialize(anterior, JsonIA.Opcoes)),
                ("user", $"Estas consultas falharam ao executar no banco:\n{erros}\nDevolva o plano completo com o SQL corrigido.")
            };
            var json = await ia.GerarJsonAsync(sistema, conversa, SchemaPlano, 0.1, ct);
            return json.Deserialize<Plano>(JsonIA.Opcoes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "IA falhou ao corrigir SQL");
            return null;
        }
    }

    /// <summary>Resumo do relatório anterior para o planejamento (pedido, tipo, consultas com SQL, seções, anexos).</summary>
    public static string DescreverAnterior(ContextoRelatorio? c)
    {
        if (c is null) return "";
        var sb = new System.Text.StringBuilder("\n\n## RELATÓRIO ANTERIOR DESTA CONVERSA (o último arquivo gerado)\n");
        sb.AppendLine($"- Pedido que o gerou: \"{c.Pedido}\"");
        sb.AppendLine($"- Título: {c.Titulo}");
        if (c.Tipo == "gerencial" && c.Parametros is { } p)
            sb.AppendLine($"- Tipo: relatorio_gerencial — município {p.Municipio}, ano {p.Ano}, meses {p.MesInicial} a {p.MesFinal}" +
                          (p.AnoComparacao is int ac ? $", comparando com {ac}" : "") +
                          $", receitas={p.IncluirReceitas}, despesas={p.IncluirDespesas}");
        if (c.Tipo == "consulta" && c.Plano is { } pl)
        {
            sb.AppendLine($"- Tipo: relatorio_consulta — municípios: {string.Join(", ", pl.Municipios)}; período: {pl.PeriodoDescricao}");
            foreach (var q in pl.Consultas)
                sb.AppendLine($"- Consulta {q.Id} ({q.Objetivo}): {q.Sql}");
            if (pl.Projecao is { } pj) sb.AppendLine($"- Projeção da consulta {pj.ConsultaId} até {pj.AteMes}/{pj.AteAno} ({pj.Metrica})");
        }
        if (c.Estrutura is { } e && c.Titulos.Count == 0)
            sb.AppendLine("- Seções: " + string.Join(" | ", e.Secoes.Select(s => s.Titulo +
                (s.Tabela is { } t ? $" [tabela da consulta {t.ConsultaId}]" : "") + (s.TabelaAnexo is not null ? " [tabela do anexo]" : ""))));
        foreach (var a in c.Acrescimos)
        {
            sb.AppendLine($"- {(a.Tipo == "alteracao" ? "Alterado" : "Acrescentado")} depois (pedido \"{a.Pedido}\"): {a.Titulo}");
            foreach (var q in a.Plano?.Consultas ?? []) sb.AppendLine($"    • consulta ({q.Objetivo}): {q.Sql}");
        }
        if (c.Titulos.Count > 0)
            sb.AppendLine("- Títulos do documento ATUAL, na ordem (use estes textos EXATOS em removerSecoes):\n" +
                          string.Join("\n", c.Titulos.Select(t => "    " + t)));
        if (c.Anexos.Count > 0) sb.AppendLine($"- Usou {c.Anexos.Count} arquivo(s) anexado(s), que continuam disponíveis e vão junto deste pedido.");
        return sb.ToString();
    }

    static string Truncar(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Corta textos "disparados" pelo modelo (título vira só a 1ª frase, até 90 caracteres).</summary>
    static Plano Limpar(Plano p)
    {
        static string? Curto(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            var t = s.Split(['.', '\n', '('], 2)[0].Trim();
            return t.Length <= max ? t : t[..max].TrimEnd() + "…";
        }
        p.Titulo = Curto(p.Titulo, 90);
        p.PeriodoDescricao = Curto(p.PeriodoDescricao, 60);
        if (p.Mensagem is { Length: > 250 }) p.Mensagem = Curto(p.Mensagem, 250);
        // Assinatura: uma linha curta de texto (nome e/ou cargo); nada de quebras ou textos longos
        p.Assinatura = string.IsNullOrWhiteSpace(p.Assinatura) ? null
            : System.Text.RegularExpressions.Regex.Replace(p.Assinatura, @"\s+", " ").Trim().Trim('"', '“', '”', '\'') is { Length: > 0 and <= 80 } a ? a : null;
        return p;
    }

    async Task<string> SistemaPlanejarAsync(IReadOnlyList<CalculadoraRelatorio.Disponibilidade> catalogo,
        Func<Task<string>> valoresDistintos, string? municipioSelecionado)
    {
        var v = Base();
        v["MUNICIPIO_SELECIONADO"] = string.IsNullOrWhiteSpace(municipioSelecionado) ? "(nenhum)" : municipioSelecionado;
        v["CATALOGO"] = TextoCatalogo(catalogo);
        v["VALORES_DISTINTOS"] = await valoresDistintos();
        return prompts.Montar(PromptStore.Planejar, v);
    }

    // ------------------------------------------------------------ Prompt 2: relatório por consulta

    public async Task<EstruturaRelatorio?> RedigirConsultaAsync(string pedido, Plano plano, IReadOnlyList<ResultadoConsulta> resultados,
        CancellationToken ct, string? notaProjecao = null, IReadOnlyList<AnexoConteudo>? anexos = null, ContextoRelatorio? anterior = null,
        string? instrucaoEdicao = null)
    {
        if (ia == null) return null;
        // Edição do documento atual (acrescentar/alterar): a instrução vem pronta do sistema, com o conteúdo atual.
        // Refazer: a estrutura do relatório anterior vai junto para manter o que não foi pedido para mudar.
        var estruturaAnterior = instrucaoEdicao is not null ? instrucaoEdicao
            : anterior?.Estrutura is { } ea
            ? $"RELATÓRIO ANTERIOR DESTA CONVERSA (pedido: \"{anterior.Pedido}\") — estrutura em JSON. Se o pedido atual é uma " +
              "alteração ou continuação dele, MANTENHA as seções, títulos e textos que o usuário não pediu para mudar e aplique só " +
              "as mudanças pedidas (os ids das consultas podem ter mudado: associe pelo objetivo):\n" +
              Truncar(JsonSerializer.Serialize(ea, JsonIA.Opcoes), 12000)
            : "";
        var listaAnexos = anexos is { Count: > 0 }
            ? "ARQUIVOS ANEXADOS: " + string.Join(", ", anexos.Select(a => a.Nome)) + " (conteúdo ao final desta mensagem)."
            : "";
        var conteudo = $"""
            Pedido do usuário: {pedido}
            Título sugerido: {plano.Titulo}
            Período: {plano.PeriodoDescricao}
            {(notaProjecao is null ? "" : "PROJEÇÃO (calculada pelo sistema — use os resultados 'projecao_mensal' e 'projecao_anual'): " + notaProjecao)}
            {listaAnexos}
            {estruturaAnterior}

            Resultados das consultas (JSON):
            {SerializarResultados(resultados, 80)}
            """;
        try
        {
            var json = await ia.GerarJsonAsync(prompts.Montar(PromptStore.Redigir, Base()), [("user", conteudo)], SchemaEstrutura, 0.3, ct, anexos);
            return json.Deserialize<EstruturaRelatorio>(JsonIA.Opcoes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "IA falhou ao redigir relatório por consulta");
            return null;
        }
    }

    // ------------------------------------------------------------ Prompt 3: textos do gerencial

    public async Task<TextosRelatorio> RedigirGerencialAsync(DadosRelatorio dados, string pedido, CancellationToken ct)
    {
        if (ia == null) return local.RedigirGerencial(dados);
        var conteudo = $"Pedido do usuário: {pedido}\n\nDados calculados (JSON):\n{JsonSerializer.Serialize(ResumoParaIA.Montar(dados), JsonIA.Opcoes)}";
        try
        {
            var json = await ia.GerarJsonAsync(prompts.Montar(PromptStore.TextosGerencial, Base()), [("user", conteudo)], SchemaTextos, 0.3, ct);
            var textos = json.Deserialize<TextosRelatorio>(JsonIA.Opcoes);
            if (textos is null) return local.RedigirGerencial(dados);
            // Nenhum quadro sem comentário: o que a IA deixar vazio é completado com o comentário padrão
            var vazios = TextosRelatorio.Campos.Count(c => string.IsNullOrWhiteSpace(
                typeof(TextosRelatorio).GetProperty(char.ToUpperInvariant(c[0]) + c[1..])?.GetValue(textos) as string));
            if (vazios > 0) log.LogInformation("IA deixou {N} campo(s) de texto vazio(s); completando com comentários padrão", vazios);
            AssistenteLocal.PreencherComentarios(dados, textos);
            return textos;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "IA falhou ao redigir textos do gerencial; usando textos padrão");
            return local.RedigirGerencial(dados);
        }
    }

    // ------------------------------------------------------------ Prompt 4: responder

    public async Task<(string? resposta, List<string> sugestoes)> ResponderAsync(string pergunta, IReadOnlyList<MensagemChat> historico,
        IReadOnlyList<ResultadoConsulta> resultados, CancellationToken ct, IReadOnlyList<AnexoConteudo>? anexos = null)
    {
        if (ia == null) return (null, []);
        var sistema = prompts.Montar(PromptStore.Responder, Base()) +
                      $"\n\n## Resultados das consultas\n{SerializarResultados(resultados, 40)}";
        try
        {
            var conversa = historico.TakeLast(6).Select(h => (h.Papel, h.Texto)).Append(("user", pergunta));
            var json = await ia.GerarJsonAsync(sistema, conversa, SchemaResposta, 0.2, ct, anexos);
            var sugestoes = json.TryGetProperty("sugestoes", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            return (json.GetProperty("resposta").GetString(), sugestoes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "IA falhou ao responder");
            return (null, []);
        }
    }

    public string ResponderResumoLocal(DadosRelatorio d) => local.ResponderResumo(d);

    static string SerializarResultados(IEnumerable<ResultadoConsulta> resultados, int maxLinhas) =>
        JsonSerializer.Serialize(resultados.Select(r => new
        {
            id = r.Id,
            objetivo = r.Objetivo,
            colunas = r.Colunas,
            linhas = r.Linhas.Take(maxLinhas),
            total_linhas = r.Linhas.Count,
            observacao = r.Truncado ? $"resultado limitado a {ExecutorSql.LimiteLinhas} linhas" :
                         r.Linhas.Count > maxLinhas ? $"mostrando {maxLinhas} de {r.Linhas.Count} linhas" : null,
            erro = r.Erro
        }), JsonIA.Opcoes);

    // ------------------------------------------------------------ schemas (formato OpenAPI aceito pelo Gemini)

    static object Str(bool nulo = false) => nulo ? new { type = "STRING", nullable = true } : new { type = "STRING" };
    static object Int() => new { type = "INTEGER", nullable = true };
    static object Bool() => new { type = "BOOLEAN", nullable = true };

    static readonly object SchemaPlano = new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["acao"] = new { type = "STRING", @enum = new[] { "relatorio_gerencial", "relatorio_consulta", "editar_relatorio", "responder", "esclarecer", "alterar_assinatura" } },
            ["mensagem"] = Str(true),
            // editar_relatorio: títulos EXATOS das seções do relatório atual a remover
            ["removerSecoes"] = new { type = "ARRAY", items = new { type = "STRING" } },
            // editar_relatorio: títulos EXATOS das seções a alterar (só elas mudam)
            ["alterarSecoes"] = new { type = "ARRAY", items = new { type = "STRING" } },
            ["trocarTabelas"] = Bool(),
            ["municipio"] = Str(true), ["municipios"] = new { type = "ARRAY", items = new { type = "STRING" } }, ["ano"] = Int(), ["mesInicial"] = Int(), ["mesFinal"] = Int(), ["anoComparacao"] = Int(),
            ["incluirReceitas"] = Bool(), ["incluirDespesas"] = Bool(),
            ["titulo"] = Str(true), ["periodoDescricao"] = Str(true), ["assinatura"] = Str(true),
            ["projecao"] = new
            {
                type = "OBJECT", nullable = true,
                properties = new Dictionary<string, object>
                {
                    ["consultaId"] = Str(), ["campoValor"] = Str(true), ["ateAno"] = new { type = "INTEGER" },
                    ["ateMes"] = new { type = "INTEGER" }, ["metrica"] = Str()
                },
                required = new[] { "consultaId", "ateAno", "metrica" }
            },
            ["consultas"] = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new Dictionary<string, object> { ["id"] = Str(), ["objetivo"] = Str(), ["sql"] = Str() },
                    required = new[] { "id", "objetivo", "sql" },
                    propertyOrdering = new[] { "id", "objetivo", "sql" }
                }
            }
        },
        // "consultas" obrigatório (pode ser lista vazia no gerencial): sem isso os modelos "lite" às vezes omitem o SQL
        // e era preciso uma 2ª chamada
        required = new[] { "acao", "consultas" },
        // consultas antes dos textos: o modelo gera o SQL primeiro
        propertyOrdering = new[] { "acao", "municipios", "alterarSecoes", "trocarTabelas", "removerSecoes", "consultas", "projecao", "titulo", "periodoDescricao", "municipio", "ano", "mesInicial", "mesFinal",
            "anoComparacao", "incluirReceitas", "incluirDespesas", "assinatura", "mensagem" }
    };

    static readonly object SchemaSoConsultas = new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["consultas"] = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new Dictionary<string, object> { ["id"] = Str(), ["objetivo"] = Str(), ["sql"] = Str() },
                    required = new[] { "id", "objetivo", "sql" },
                    propertyOrdering = new[] { "id", "objetivo", "sql" }
                }
            }
        },
        required = new[] { "consultas" }
    };

    static readonly object SchemaEstrutura = new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["titulo"] = Str(), ["subtitulo"] = Str(), ["introducao"] = Str(), ["conclusao"] = Str(),
            ["sugestoes"] = SchemaSugestoes,
            ["secoes"] = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new Dictionary<string, object>
                    {
                        ["titulo"] = Str(), ["textoAntes"] = Str(), ["textoDepois"] = Str(),
                        ["grafico"] = new
                        {
                            type = "OBJECT", nullable = true,
                            properties = new Dictionary<string, object>
                            {
                                ["consultaId"] = Str(), ["titulo"] = Str(),
                                ["tipo"] = new { type = "STRING", @enum = new[] { "colunas", "barras", "linha", "pizza" } },
                                ["campoCategoria"] = Str(),
                                ["camposValor"] = new { type = "ARRAY", items = new { type = "STRING" } },
                                ["nomesSeries"] = new { type = "ARRAY", items = new { type = "STRING" } },
                                ["formato"] = new { type = "STRING", @enum = new[] { "moeda", "numero", "percentual" } }
                            },
                            required = new[] { "consultaId", "titulo", "tipo", "campoCategoria", "camposValor" }
                        },
                        ["tabela"] = new
                        {
                            type = "OBJECT", nullable = true,
                            properties = new Dictionary<string, object>
                            {
                                ["consultaId"] = Str(), ["titulo"] = Str(), ["linhaTotal"] = new { type = "BOOLEAN" },
                                ["colunas"] = new
                                {
                                    type = "ARRAY",
                                    items = new
                                    {
                                        type = "OBJECT",
                                        properties = new Dictionary<string, object>
                                        {
                                            ["campo"] = Str(), ["rotulo"] = Str(),
                                            ["formato"] = new { type = "STRING", @enum = new[] { "texto", "moeda", "numero", "percentual", "participacao", "variacao" } },
                                            ["campoBase"] = Str(true)
                                        },
                                        required = new[] { "campo", "rotulo", "formato" }
                                    }
                                }
                            },
                            required = new[] { "consultaId", "titulo", "colunas", "linhaTotal" }
                        },
                        // IMAGEM ANEXADA (foto/print) inserida no documento como está
                        ["imagemAnexo"] = new
                        {
                            type = "OBJECT", nullable = true,
                            properties = new Dictionary<string, object> { ["arquivo"] = Str(), ["legenda"] = Str() },
                            required = new[] { "arquivo", "legenda" }
                        },
                        // Tabela transcrita de um ARQUIVO ANEXADO (valores como estão no arquivo)
                        ["tabelaAnexo"] = new
                        {
                            type = "OBJECT", nullable = true,
                            properties = new Dictionary<string, object>
                            {
                                ["titulo"] = Str(), ["fonte"] = Str(),
                                ["cabecalho"] = new { type = "ARRAY", items = new { type = "STRING" } },
                                ["linhas"] = new { type = "ARRAY", items = new { type = "ARRAY", items = new { type = "STRING" } } }
                            },
                            required = new[] { "titulo", "fonte", "cabecalho", "linhas" }
                        }
                    },
                    required = new[] { "titulo", "textoAntes", "textoDepois" },
                    propertyOrdering = new[] { "titulo", "textoAntes", "imagemAnexo", "tabela", "tabelaAnexo", "grafico", "textoDepois" }
                }
            }
        },
        required = new[] { "titulo", "subtitulo", "introducao", "secoes", "conclusao", "sugestoes" },
        propertyOrdering = new[] { "titulo", "subtitulo", "introducao", "secoes", "conclusao", "sugestoes" }
    };

    // Propriedade (não campo): os campos estáticos acima a usam e são inicializados antes desta linha
    static object SchemaSugestoes => new { type = "ARRAY", items = new { type = "STRING" } };

    // Um campo de texto por seção/quadro do relatório gerencial (ordem = ordem do documento)
    static readonly object SchemaTextos = new
    {
        type = "OBJECT",
        properties = TextosRelatorio.Campos.ToDictionary(c => c, _ => (object)new { type = "STRING" })
            .Append(new KeyValuePair<string, object>("sugestoes", new { type = "ARRAY", items = new { type = "STRING" } }))
            .ToDictionary(),
        required = TextosRelatorio.Campos.Append("sugestoes").ToArray(),
        propertyOrdering = TextosRelatorio.Campos.Append("sugestoes").ToArray()
    };

    static readonly object SchemaResposta = new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object> { ["resposta"] = Str(), ["sugestoes"] = SchemaSugestoes },
        required = new[] { "resposta", "sugestoes" }
    };
}
