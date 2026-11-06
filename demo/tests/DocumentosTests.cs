using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using RelatorioIA.Api.Models;
using RelatorioIA.Api.Services;
using RelatorioIA.Api.Services.Consultas;
using RelatorioIA.Api.Services.Documentos;
using RelatorioIA.Api.Services.IA;

namespace RelatorioIA.Tests;

[Collection("banco")]
public class DocumentosTests(BancoFixture f)
{
    static readonly IOptions<RelatorioOptions> Opt = Options.Create(new RelatorioOptions());

    async Task<Documento> Gerencial()
    {
        await using var db = f.NovoContexto();
        var dados = await f.Calculadora(db).CalcularAsync(new ParametrosRelatorio("Águas da Prata", 2026, 1, 5, 2025));
        return new MontadorGerencial(Opt).Montar(dados, new AssistenteLocal().RedigirGerencial(dados));
    }

    [Fact]
    public async Task Relatorio_gerencial_gera_Word_valido_com_os_valores_oficiais()
    {
        var bytes = new RenderizadorDocx(Opt).Renderizar(await Gerencial());
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var texto = doc.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("R$ 27.196.080,13", texto);
        Assert.Contains("R$ 31.232.865,51", texto);
        Assert.Contains("R$ (4.036.785,38)", texto);   // resultado receita − despesa
        Assert.Contains("Análise Comparativa dos Anos 2025 e 2026", texto);
        // Exigência da gerência: parágrafo fixo na conclusão
        Assert.Contains("caráter puramente quantitativo", texto);
        Assert.Contains("reuniões com os gestores de cada área", texto);
    }

    [Fact]
    public async Task Nenhum_quadro_do_gerencial_fica_sem_comentario()
    {
        await using var db = f.NovoContexto();
        var dados = await f.Calculadora(db).CalcularAsync(new ParametrosRelatorio("Águas da Prata", 2026, 1, 5, 2025));
        var t = new AssistenteLocal().RedigirGerencial(dados);
        foreach (var campo in TextosRelatorio.Campos)
        {
            var valor = typeof(TextosRelatorio).GetProperty(char.ToUpperInvariant(campo[0]) + campo[1..])!.GetValue(t) as string;
            Assert.False(string.IsNullOrWhiteSpace(valor), $"campo sem texto: {campo}");
        }
        // Comentário com números reais (ex.: órgãos da receita: Prefeitura 85,66% — conforme o relatório oficial)
        Assert.Contains("85,66%", t.ComentarioOrgaosReceita);

        // IA devolveu só a introdução → os demais campos são completados
        var parcial = new TextosRelatorio { Introducao = "x" };
        AssistenteLocal.PreencherComentarios(dados, parcial);
        Assert.False(string.IsNullOrWhiteSpace(parcial.ComentarioModalidade));
        Assert.Equal("x", parcial.Introducao);
    }

    [Fact]
    public void Colunas_de_valor_nao_quebram_valores_grandes()
    {
        var linhas = new List<string[]> { new[] { "TESOURO", "R$ 19.254.904,71", "70,80%" } };
        var w = MontadorGerencial.LargurasAuto(linhas, 8);
        Assert.Equal(100, w.Sum());
        Assert.True(w[1] >= 18, $"coluna de valor estreita demais: {w[1]}%");   // 16 caracteres a 8pt ≈ 18,6%
    }

    [Fact]
    public async Task Relatorio_gerencial_tem_grafico_em_cada_modulo_com_tabela()
    {
        var doc = await Gerencial();
        // Cada módulo (do título até o próximo título) que tem tabela termina com ao menos um gráfico
        var modulos = new List<List<Bloco>>();
        foreach (var b in doc.Blocos)
            if (b is Titulo) modulos.Add([b]); else if (modulos.Count > 0) modulos[^1].Add(b);
        var semGrafico = modulos.Where(m => m.OfType<TabelaDoc>().Any() && !m.OfType<GraficoDoc>().Any())
            .Select(m => ((Titulo)m[0]).Texto).ToList();
        Assert.Empty(semGrafico);

        var graficos = doc.Blocos.OfType<GraficoDoc>().Count();
        var bytes = new RenderizadorDocx(Opt).Renderizar(doc);
        using var w = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        Assert.Equal(graficos + 1, w.MainDocumentPart!.ImageParts.Count());   // + a capa (página desenhada)
    }

    [Fact]
    public void Grafico_gera_PNG_valido_para_cada_tipo()
    {
        foreach (var tipo in Enum.GetValues<TipoGrafico>())
        {
            var (png, prop) = GeradorGraficos.Desenhar(new GraficoDados(tipo, ["JAN", "FEV", "MAR"],
                [new SerieGrafico("Receita", [100, 250.5, -30]), new SerieGrafico("Despesa", [80, 300, 10])]));
            Assert.Equal(0x89, png[0]); Assert.Equal((byte)'P', png[1]);   // assinatura PNG
            Assert.True(prop is > 0.1 and < 2);
        }
    }

    [Fact]
    public void Rosca_agrupa_excedente_em_DEMAIS_e_soma_o_total()
    {
        var itens = new (string, decimal)[] { ("A", 50), ("B", 20), ("C", 10), ("D", 8), ("E", 7), ("F", 5) };
        var g = Graficos.Rosca(itens, 100m, "DEMAIS FONTES");
        Assert.Equal(["A", "B", "C", "D", "DEMAIS FONTES"], g.Categorias);
        Assert.Equal(12, g.Series[0].Valores[^1]);                 // E + F
        Assert.Equal(100, g.Series[0].Valores.Sum());
        Assert.Equal("R$ 50,00 · 50,00%", g.RotulosFim![0]);
        Assert.NotEmpty(GeradorGraficos.Desenhar(g).png);
    }

    [Fact]
    public void Grafico_pedido_pela_IA_e_validado_contra_os_dados()
    {
        var r = Resultado();
        // campos válidos → gráfico
        Assert.NotNull(MontadorConsulta.MontarGrafico(new GraficoPlanejado { ConsultaId = "c1", Tipo = "barras", CampoCategoria = "credor", CamposValor = ["pago"] }, r));
        // campo inexistente ou não numérico → sem gráfico (não quebra o relatório)
        Assert.Null(MontadorConsulta.MontarGrafico(new GraficoPlanejado { ConsultaId = "c1", CampoCategoria = "credor", CamposValor = ["inexistente"] }, r));
        Assert.Null(MontadorConsulta.MontarGrafico(new GraficoPlanejado { ConsultaId = "c1", CampoCategoria = "pago", CamposValor = ["credor"] }, r));
    }

    [Fact]
    public async Task Assinatura_fica_presa_ao_texto_de_encerramento()
    {
        var doc = await Gerencial();
        // Do texto de encerramento até a linha da assinatura, tudo "mantém com o próximo"; o nome fecha o bloco
        var ini = doc.Blocos.FindLastIndex(b => b is Paragrafo p && p.Texto.StartsWith("Este relatório gerencial apresenta"));
        var bloco = doc.Blocos.Skip(ini - 1).Cast<Paragrafo>().ToList();   // inclui o último parágrafo da conclusão
        Assert.Contains("caráter puramente quantitativo", bloco[0].Texto);
        Assert.True(bloco.Count >= 5);
        Assert.All(bloco[..^1], p => Assert.True(p.ManterComProximo, $"solto: '{p.Texto}'"));
        Assert.False(bloco[^1].ManterComProximo);

        // Trocar a assinatura mantém o encadeamento
        doc.TrocarAssinatura("Gestor responsável");
        Assert.True(((Paragrafo)doc.Blocos[^2]).ManterComProximo);

        using var w = WordprocessingDocument.Open(new MemoryStream(new RenderizadorDocx(Opt).Renderizar(doc)), false);
        var pars = w.MainDocumentPart!.Document.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().ToList();
        var iTexto = pars.FindLastIndex(p => p.InnerText.StartsWith("Este relatório gerencial apresenta"));
        Assert.All(pars[iTexto..^1], p => Assert.NotNull(p.ParagraphProperties?.KeepNext));
        Assert.Equal("GESTOR RESPONSÁVEL", pars[^1].InnerText);
    }

    [Fact]
    public async Task Relatorio_gerencial_gera_PDF_valido()
    {
        var bytes = new RenderizadorPdf(Opt).Renderizar(await Gerencial());
        Assert.True(bytes.Length > 20_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    static ResultadoConsulta Resultado() => new()
    {
        Id = "c1", Objetivo = "Ranking", Sql = "SELECT ...",
        Colunas = ["credor", "empenhado", "pago"],
        Linhas = [["A", 300.0, 200.0], ["B", 100.0, 50.0]]
    };

    [Fact]
    public void Relatorio_por_consulta_preenche_tabela_com_dados_reais_e_participacao()
    {
        var e = new EstruturaRelatorio
        {
            Titulo = "Teste", Subtitulo = "Sub", Introducao = "Intro.", Conclusao = "Fim.",
            Secoes =
            [
                new SecaoRelatorio
                {
                    Titulo = "Seção", TextoAntes = "Antes.", TextoDepois = "O credor A recebeu R$ 200,00 e a diferença foi R$ 100,00.",
                    Tabela = new TabelaPlanejada
                    {
                        ConsultaId = "c1", Titulo = "tabela", LinhaTotal = true,
                        Colunas =
                        [
                            new() { Campo = "credor", Rotulo = "CREDOR", Formato = "texto" },
                            new() { Campo = "pago", Rotulo = "PAGO", Formato = "moeda" },
                            new() { Campo = "part", Rotulo = "%", Formato = "participacao", CampoBase = "pago" },
                            new() { Campo = "inexistente", Rotulo = "X", Formato = "moeda" }   // ignorada
                        ]
                    }
                }
            ]
        };
        var (doc, naoConferidos) = new MontadorConsulta(Opt).Montar(e, new Plano { PeriodoDescricao = "Jan a Mai" }, [Resultado()], "Águas da Prata", []);
        var tab = doc.Blocos.OfType<TabelaDoc>().Single();
        Assert.Equal(["CREDOR", "PAGO", "%"], tab.Cabecalho!);
        Assert.Equal(["A", "R$ 200,00", "80,00%"], tab.Linhas[0]);
        Assert.Equal(["TOTAL", "R$ 250,00", "100,00%"], tab.Linhas[^1]);
        Assert.Empty(naoConferidos);   // 200,00 está nos dados; 100,00 = 300 − 200 (diferença na mesma linha)
    }

    static TabelaDoc TabelaDe(ResultadoConsulta r, bool linhaTotal, params ColunaPlanejada[] cols)
    {
        var e = new EstruturaRelatorio
        {
            Titulo = "T", Secoes = [new SecaoRelatorio { Titulo = "S", Tabela = new TabelaPlanejada { ConsultaId = r.Id, LinhaTotal = linhaTotal, Colunas = [.. cols] } }]
        };
        var (doc, _) = new MontadorConsulta(Opt).Montar(e, new Plano(), [r], "X", []);
        return doc.Blocos.OfType<TabelaDoc>().Single();
    }

    [Fact]
    public void Comparativo_entre_anos_uma_coluna_por_ano_vira_anos_no_cabecalho_com_percentual()
    {
        var r = new ResultadoConsulta
        {
            Id = "c1", Objetivo = "o", Sql = "s", Colunas = ["funcao", "pago_2026", "pago_2025"],   // ordem trocada de propósito
            Linhas = [["Saúde", 120.0, 100.0], ["Educação", 150.0, 200.0]]
        };
        var tab = TabelaDe(r, false,
            new() { Campo = "funcao", Rotulo = "FUNÇÃO" }, new() { Campo = "pago_2026", Rotulo = "2026", Formato = "moeda" },
            new() { Campo = "pago_2025", Rotulo = "2025", Formato = "moeda" });
        Assert.Equal(["FUNÇÃO", "2025", "2026", "%"], tab.Cabecalho!);   // % só na evolução 2025→2026
        Assert.Equal(["Saúde", "R$ 100,00", "R$ 120,00", "20,00%"], tab.Linhas[0]);
        Assert.Equal("-25,00%", tab.Linhas[1][3]);
        Assert.Equal(["TOTAL", "R$ 300,00", "R$ 270,00", ""], tab.Linhas[^1]);   // total só dos valores
    }

    [Fact]
    public void Comparativo_entre_anos_uma_linha_por_ano_vira_informacoes_nas_linhas()
    {
        // ano, empenhado, pago (uma linha por ano) → linhas Empenhado/Pago, colunas 2023 % 2024 % 2025 % TOTAL %
        var r = new ResultadoConsulta
        {
            Id = "c1", Objetivo = "o", Sql = "s", Colunas = ["ano", "empenhado", "pago"],
            Linhas = [[2023, 100.0, 80.0], [2024, 110.0, 88.0], [2025, 121.0, 110.0]]
        };
        var tab = TabelaDe(r, true, new() { Campo = "ano", Rotulo = "ANO" },
            new() { Campo = "empenhado", Rotulo = "EMPENHADO", Formato = "moeda" }, new() { Campo = "pago", Rotulo = "PAGO", Formato = "moeda" });
        Assert.Equal(["DESCRIÇÃO", "2023", "2024", "%", "2025", "%", "TOTAL %"], tab.Cabecalho!);
        Assert.Equal(["Empenhado", "R$ 100,00", "R$ 110,00", "10,00%", "R$ 121,00", "10,00%", "21,00%"], tab.Linhas[0]);
        Assert.Equal(["Pago", "R$ 80,00", "R$ 88,00", "10,00%", "R$ 110,00", "25,00%", "37,50%"], tab.Linhas[1]);
        Assert.Equal(["TOTAL", "R$ 180,00", "R$ 198,00", "", "R$ 231,00", "", ""], tab.Linhas[^1]);
    }

    [Fact]
    public void Ranking_sem_percentual_ganha_participacao_e_total_100()
    {
        var tab = TabelaDe(new ResultadoConsulta { Id = "c1", Objetivo = "o", Sql = "s", Colunas = ["credor", "pago"], Linhas = [["A", 300.0], ["B", 100.0]] },
            false, new() { Campo = "credor", Rotulo = "CREDOR" }, new() { Campo = "pago", Rotulo = "PAGO", Formato = "moeda" });
        Assert.Equal(["CREDOR", "PAGO", "PART. %"], tab.Cabecalho!);
        Assert.Equal(["A", "R$ 300,00", "75,00%"], tab.Linhas[0]);
        Assert.Equal(["TOTAL", "R$ 400,00", "100,00%"], tab.Linhas[^1]);
    }

    [Fact]
    public void Serie_por_mes_compara_com_a_linha_anterior_e_percentual_vai_para_o_fim()
    {
        var r = new ResultadoConsulta { Id = "c1", Objetivo = "o", Sql = "s", Colunas = ["mes_nome", "total"], Linhas = [["Janeiro", 100.0], ["Fevereiro", 120.0]] };
        var tab = TabelaDe(r, true, new() { Campo = "mes_nome", Rotulo = "MÊS" }, new() { Campo = "total", Rotulo = "TOTAL PAGO", Formato = "moeda" });
        Assert.Equal(["Janeiro", "R$ 100,00", "-"], tab.Linhas[0]);
        Assert.Equal(["Fevereiro", "R$ 120,00", "20,00%"], tab.Linhas[1]);
        Assert.Equal(["TOTAL", "R$ 220,00", ""], tab.Linhas[^1]);

        // ano × mês (uma métrica): meses nas linhas, anos no cabeçalho
        var r3 = new ResultadoConsulta { Id = "c1", Objetivo = "o", Sql = "s", Colunas = ["ano", "mes", "valor"],
            Linhas = [[2024, 1, 10.0], [2024, 2, 20.0], [2025, 1, 15.0], [2025, 2, 10.0]] };
        var t3 = TabelaDe(r3, true, new() { Campo = "mes", Rotulo = "MÊS" }, new() { Campo = "valor", Rotulo = "VALOR", Formato = "moeda" });
        Assert.Equal(["MÊS", "2024", "2025", "%"], t3.Cabecalho!);
        Assert.Equal(["JANEIRO", "R$ 10,00", "R$ 15,00", "50,00%"], t3.Linhas[0].Select((c, i) => i == 0 ? c.ToUpper() : c));
        Assert.Equal(["TOTAL", "R$ 30,00", "R$ 25,00", ""], t3.Linhas[^1]);

        // % que a IA pôs no meio vai para a última coluna
        var r2 = new ResultadoConsulta { Id = "c1", Objetivo = "o", Sql = "s", Colunas = ["orgao", "perc", "valor"], Linhas = [["P", 60.0, 6.0], ["C", 40.0, 4.0]] };
        var t2 = TabelaDe(r2, true, new() { Campo = "orgao", Rotulo = "ÓRGÃO" }, new() { Campo = "perc", Rotulo = "%", Formato = "percentual" },
            new() { Campo = "valor", Rotulo = "VALOR", Formato = "moeda" });
        Assert.Equal(["ÓRGÃO", "VALOR", "%"], t2.Cabecalho!);
        Assert.Equal(["TOTAL", "R$ 10,00", "100,00%"], t2.Linhas[^1]);
    }

    [Fact]
    public void Relatorio_por_consulta_sinaliza_valor_inventado_pela_IA()
    {
        var e = new EstruturaRelatorio
        {
            Titulo = "T", Introducao = "", Conclusao = "O total foi R$ 999.999,99.",
            Secoes = [new SecaoRelatorio { Titulo = "S", Tabela = new TabelaPlanejada { ConsultaId = "c1", Colunas = [] } }]
        };
        var (_, naoConferidos) = new MontadorConsulta(Opt).Montar(e, new Plano(), [Resultado()], "X", []);
        Assert.Equal(["999.999,99"], naoConferidos);
    }

    [Fact]
    public void Relatorio_por_consulta_com_resultado_vazio_nao_quebra()
    {
        var vazio = new ResultadoConsulta { Id = "c1", Objetivo = "o", Sql = "s", Colunas = ["a"] };
        var e = new EstruturaRelatorio { Titulo = "T", Secoes = [new SecaoRelatorio { Titulo = "S", Tabela = new TabelaPlanejada { ConsultaId = "c1" } }] };
        var (doc, _) = new MontadorConsulta(Opt).Montar(e, new Plano(), [vazio], "X", []);
        Assert.Contains(doc.Blocos.OfType<Paragrafo>(), p => p.Texto.Contains("Não houve registros"));
        Assert.NotEmpty(new RenderizadorPdf(Opt).Renderizar(doc));
        Assert.NotEmpty(new RenderizadorDocx(Opt).Renderizar(doc));
    }
}
