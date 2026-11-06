using Microsoft.EntityFrameworkCore;

namespace RelatorioIA.Api.Data;

/// <summary>
/// Acesso SOMENTE LEITURA ao banco do painel (Tcudb, SQL Server), direto nas views do TCESP.
/// O usuário do banco (dev_report) só tem SELECT nessas views.
/// </summary>
public class RelatorioDbContext(DbContextOptions<RelatorioDbContext> options) : DbContext(options)
{
    public DbSet<Despesa> Despesas => Set<Despesa>();
    public DbSet<Receita> Receitas => Set<Receita>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Despesa>(e =>
        {
            e.ToView("vw_DespesaCompleta", "dbo");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("IdDespesaCompleta");
            e.Property(x => x.Mes).HasColumnName("MesReferencia");
            e.Property(x => x.NrEmpenho).HasColumnName("NumeroEmpenho");
            e.Property(x => x.Credor).HasColumnName("DescricaoDespesa");
            e.Property(x => x.Valor).HasColumnName("ValorDespesa").HasPrecision(18, 2);
            e.Property(x => x.Funcao).HasColumnName("FuncaoGoverno");
            e.Property(x => x.Subfuncao).HasColumnName("SubfuncaoGoverno");
            e.Property(x => x.CdPrograma).HasColumnName("CodigoPrograma");
            e.Property(x => x.Programa).HasColumnName("DescricaoPrograma");
            e.Property(x => x.CdAcao).HasColumnName("CodigoAcao");
            e.Property(x => x.Acao).HasColumnName("DescricaoAcao");
            e.Property(x => x.CodAplicacao).HasColumnName("CodigoAplicacao");
            e.Property(x => x.Modalidade).HasColumnName("ModalidadeLicitacao");
            e.Property(x => x.Historico).HasColumnName("HistoricoDespesa");
        });
        b.Entity<Receita>(e =>
        {
            e.ToView("vw_ReceitaCompleta", "dbo");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("IdReceitaCompleta");
            e.Property(x => x.Mes).HasColumnName("MesReferencia");
            e.Property(x => x.Poder).HasColumnName("DsPoder");
            e.Property(x => x.CodAplicacaoFixo).HasColumnName("DsAplicacaoFixa");
            e.Property(x => x.CodAplicacaoVariavel).HasColumnName("DsAplicacaoVariavel");
            e.Property(x => x.Categoria).HasColumnName("CategoriaReceita");
            e.Property(x => x.Subcategoria).HasColumnName("SubCategoriaReceita");
            e.Property(x => x.Fonte).HasColumnName("DsFonte");
            e.Property(x => x.Rubrica).HasColumnName("DsRubrica");
            e.Property(x => x.Alinea).HasColumnName("DsAlinea");
            e.Property(x => x.Subalinea).HasColumnName("DsSubAlinea");
            e.Property(x => x.Tipo).HasColumnName("DsTipo");
            e.Property(x => x.Valor).HasColumnName("ValorReceita").HasPrecision(18, 2);
        });
    }
}
