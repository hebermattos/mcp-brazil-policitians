using System.ComponentModel;
using McpBrazilPoliticians.Services;
using ModelContextProtocol.Server;

namespace McpBrazilPoliticians.Tools;

[McpServerToolType]
public static class EventosOrgaosTools
{
    [McpServerTool, Description("Searches Câmara events such as sessions, meetings, hearings, seminars, and other legislative events.")]
    public static Task<string> SearchEventosAsync(
        [Description("Start date in yyyy-MM-dd format.")] string? dataInicio = null,
        [Description("End date in yyyy-MM-dd format.")] string? dataFim = null,
        [Description("Câmara body/committee id.")] int? idOrgao = null,
        [Description("Event situation. Example: Confirmada, Encerrada, Cancelada.")] string? situacao = null,
        [Description("Page number. Starts at 1.")] int pagina = 1,
        [Description("Items per page. Recommended: 10 to 100.")] int itens = 20,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync("eventos", new Dictionary<string, object?>
        {
            ["dataInicio"] = dataInicio,
            ["dataFim"] = dataFim,
            ["idOrgao"] = idOrgao,
            ["situacao"] = situacao,
            ["pagina"] = pagina,
            ["itens"] = itens,
            ["ordem"] = "ASC",
            ["ordenarPor"] = "dataHoraInicio"
        }, cancellationToken);
    }

    [McpServerTool, Description("Gets one Câmara event by id.")]
    public static Task<string> GetEventoAsync(
        [Description("Event id from Dados Abertos Câmara.")] int idEvento,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"eventos/{idEvento}", cancellationToken: cancellationToken);
    }

    [McpServerTool, Description("Searches Câmara bodies/committees, including Plenário, Mesa Diretora, permanent committees, temporary committees, CPIs, and councils.")]
    public static Task<string> SearchOrgaosAsync(
        [Description("Body abbreviation. Example: CCJC, CFT, PLEN.")] string? sigla = null,
        [Description("Text fragment from the body name.")] string? nome = null,
        [Description("Page number. Starts at 1.")] int pagina = 1,
        [Description("Items per page. Recommended: 10 to 100.")] int itens = 20,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync("orgaos", new Dictionary<string, object?>
        {
            ["sigla"] = sigla,
            ["nome"] = nome,
            ["pagina"] = pagina,
            ["itens"] = itens,
            ["ordem"] = "ASC",
            ["ordenarPor"] = "sigla"
        }, cancellationToken);
    }

    [McpServerTool, Description("Gets one Câmara body/committee by id.")]
    public static Task<string> GetOrgaoAsync(
        [Description("Body/committee id from Dados Abertos Câmara.")] int idOrgao,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"orgaos/{idOrgao}", cancellationToken: cancellationToken);
    }

    [McpServerTool, Description("Gets members of one Câmara body/committee.")]
    public static Task<string> GetOrgaoMembrosAsync(
        [Description("Body/committee id from Dados Abertos Câmara.")] int idOrgao,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"orgaos/{idOrgao}/membros", cancellationToken: cancellationToken);
    }
}
