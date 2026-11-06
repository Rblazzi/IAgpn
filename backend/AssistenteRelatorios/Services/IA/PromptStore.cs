namespace RelatorioIA.Api.Services.IA;

/// <summary>
/// Lê os prompts padrão da pasta Prompts/ a cada uso (editar o .md vale na hora, sem recompilar)
/// e substitui os marcadores {{NOME}}.
/// </summary>
public class PromptStore(IWebHostEnvironment env)
{
    public const string Identidade = "00-identidade.md";
    public const string Planejar = "01-planejar-consulta.md";
    public const string Redigir = "02-redigir-relatorio.md";
    public const string TextosGerencial = "03-textos-gerencial.md";
    public const string Responder = "04-responder.md";
    public const string Sugestoes = "05-sugestoes.md";

    /// <summary>
    /// Pasta dos prompts: na raiz do site (módulo copiado dentro do projeto da API ou publicado) ou, quando o módulo é
    /// um projeto referenciado, na pasta de saída (bin), para onde o AssistenteRelatorios.csproj copia os .md.
    /// </summary>
    public string Pasta { get; } = new[]
    {
        Path.Combine(env.ContentRootPath, "AssistenteRelatorios", "Prompts"),
        Path.Combine(AppContext.BaseDirectory, "AssistenteRelatorios", "Prompts")
    }.FirstOrDefault(p => File.Exists(Path.Combine(p, Planejar)))
      ?? Path.Combine(env.ContentRootPath, "AssistenteRelatorios", "Prompts");

    public string Montar(string arquivo, IReadOnlyDictionary<string, string> valores)
    {
        var texto = File.ReadAllText(Path.Combine(Pasta, arquivo));
        foreach (var (chave, valor) in valores) texto = texto.Replace("{{" + chave + "}}", valor);
        return texto;
    }
}
