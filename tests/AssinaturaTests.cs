using RelatorioIA.Api.Models;
using RelatorioIA.Api.Services.Documentos;

namespace RelatorioIA.Tests;

/// <summary>Linha de assinatura da última página: pedida no chat → configuração (Responsavel) → consultoria.</summary>
public class AssinaturaTests
{
    static string Ultima(RelatorioOptions o, string? assinatura)
    {
        var doc = new Documento { Titulo = "T", Municipio = "Águas da Prata" };
        MontadorGerencial.Encerramento(doc, o, "Águas da Prata", assinatura);
        return doc.Blocos.OfType<Paragrafo>().Last().Texto;
    }

    [Fact]
    public void Usa_a_assinatura_pedida_no_chat() =>
        Assert.Equal("GESTOR RESPONSÁVEL", Ultima(new RelatorioOptions(), "Gestor responsável"));

    [Fact]
    public void Documento_salvo_em_JSON_reabre_e_troca_so_a_assinatura()
    {
        var doc = new Documento { Titulo = "RELATÓRIO", Municipio = "Águas da Prata", PeriodoAnalisado = "Jan a Mai" };
        doc.Brasoes.Add([1, 2, 3]);
        doc.T1("Seção").P("Texto.").Tab(new TabelaDoc { Linhas = [["a", "1"]], Larguras = [50, 50], Cabecalho = ["X", "Y"] });
        doc.Blocos.Add(new GraficoDoc("Gráfico", [9, 9], 0.5));
        MontadorGerencial.Encerramento(doc, new RelatorioOptions(), "Águas da Prata");

        var json = System.Text.Json.JsonSerializer.Serialize(doc);
        var lido = System.Text.Json.JsonSerializer.Deserialize<Documento>(json)!;
        lido.TrocarAssinatura("Gestor responsável");

        Assert.Equal(doc.Blocos.Count, lido.Blocos.Count);
        Assert.Single(lido.Brasoes);
        Assert.IsType<TabelaDoc>(lido.Blocos[2]);
        Assert.IsType<GraficoDoc>(lido.Blocos[3]);
        Assert.Equal("GESTOR RESPONSÁVEL", lido.Blocos.OfType<Paragrafo>().Last().Texto);
        Assert.Equal(doc.Blocos.OfType<Paragrafo>().First().Texto, lido.Blocos.OfType<Paragrafo>().First().Texto);
    }

    [Fact]
    public void Sem_pedido_usa_o_responsavel_configurado_ou_a_consultoria()
    {
        Assert.Equal("GPN CONSULTORIA", Ultima(new RelatorioOptions(), null));
        Assert.Equal("JOÃO SOUZA", Ultima(new RelatorioOptions { Responsavel = "João Souza" }, ""));
    }
}
