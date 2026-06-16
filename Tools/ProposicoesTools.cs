using System.ComponentModel;
using McpBrazilPoliticians.Services;
using ModelContextProtocol.Server;

namespace McpBrazilPoliticians.Tools;

[McpServerToolType]
public static class ProposicoesTools
{
    [McpServerTool, Description("Searches legislative propositions in Câmara Dados Abertos.")]
    public static Task<string> SearchProposicoesAsync(
        [Description("Proposition type abbreviation. Example: PL, PEC, REQ, PDL.")] string? siglaTipo = null,
        [Description("Proposition number.")] int? numero = null,
        [Description("Presentation year. Example: 2026.")] int? ano = null,
        [Description("Text fragment from the proposition summary/ementa.")] string? ementa = null,
        [Description("Author id, when known.")] int? idAutor = null,
        [Description("Start presentation date in yyyy-MM-dd format.")] string? dataApresentacaoInicio = null,
        [Description("End presentation date in yyyy-MM-dd format.")] string? dataApresentacaoFim = null,
        [Description("Page number. Starts at 1.")] int pagina = 1,
        [Description("Items per page. Recommended: 10 to 50.")] int itens = 20,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync("proposicoes", new Dictionary<string, object?>
        {
            ["siglaTipo"] = siglaTipo,
            ["numero"] = numero,
            ["ano"] = ano,
            ["ementa"] = ementa,
            ["idAutor"] = idAutor,
            ["dataApresentacaoInicio"] = dataApresentacaoInicio,
            ["dataApresentacaoFim"] = dataApresentacaoFim,
            ["pagina"] = pagina,
            ["itens"] = itens,
            ["ordem"] = "DESC",
            ["ordenarPor"] = "id"
        }, cancellationToken);
    }

    [McpServerTool, Description("Gets a legislative proposition by id.")]
    public static Task<string> GetProposicaoAsync(
        [Description("Proposition id from Dados Abertos Câmara.")] int idProposicao,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"proposicoes/{idProposicao}", cancellationToken: cancellationToken);
    }

    [McpServerTool, Description("Gets authors of a legislative proposition.")]
    public static Task<string> GetProposicaoAutoresAsync(
        [Description("Proposition id from Dados Abertos Câmara.")] int idProposicao,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"proposicoes/{idProposicao}/autores", cancellationToken: cancellationToken);
    }

    [McpServerTool, Description("Gets the processing/status history for a legislative proposition.")]
    public static Task<string> GetProposicaoTramitacoesAsync(
        [Description("Proposition id from Dados Abertos Câmara.")] int idProposicao,
        [Description("Page number. Starts at 1.")] int pagina = 1,
        [Description("Items per page. Recommended: 10 to 100.")] int itens = 20,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"proposicoes/{idProposicao}/tramitacoes", new Dictionary<string, object?>
        {
            ["pagina"] = pagina,
            ["itens"] = itens
        }, cancellationToken);
    }

    [McpServerTool, Description("Gets propositions related to another proposition.")]
    public static Task<string> GetProposicoesRelacionadasAsync(
        [Description("Proposition id from Dados Abertos Câmara.")] int idProposicao,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"proposicoes/{idProposicao}/relacionadas", cancellationToken: cancellationToken);
    }
}
