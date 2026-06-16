using System.ComponentModel;
using McpBrazilPoliticians.Services;
using ModelContextProtocol.Server;

namespace McpBrazilPoliticians.Tools;

[McpServerToolType]
public static class DeputadosTools
{
    [McpServerTool, Description("Searches Brazilian federal deputies in Câmara dos Deputados Dados Abertos by name, state, party, legislature, and pagination.")]
    public static Task<string> SearchDeputadosAsync(
        [Description("Partial parliamentary name. Example: Maria, Silva, Boulos.")] string? nome = null,
        [Description("Brazilian state abbreviation. Example: RS, SP, RJ.")] string? siglaUf = null,
        [Description("Party abbreviation. Example: PT, PL, MDB.")] string? siglaPartido = null,
        [Description("Legislature id. Current legislatures can be discovered with the legislaturas endpoint.")] int? idLegislatura = null,
        [Description("Page number. Starts at 1.")] int pagina = 1,
        [Description("Items per page. Use small values for LLM responses. Recommended: 10 to 50.")] int itens = 20,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync("deputados", new Dictionary<string, object?>
        {
            ["nome"] = nome,
            ["siglaUf"] = siglaUf,
            ["siglaPartido"] = siglaPartido,
            ["idLegislatura"] = idLegislatura,
            ["pagina"] = pagina,
            ["itens"] = itens,
            ["ordem"] = "ASC",
            ["ordenarPor"] = "nome"
        }, cancellationToken);
    }

    [McpServerTool, Description("Gets the detailed profile for one Brazilian federal deputy by Câmara Dados Abertos deputy id.")]
    public static Task<string> GetDeputadoAsync(
        [Description("Deputy id from Dados Abertos Câmara. Example: 220593.")] int idDeputado,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"deputados/{idDeputado}", cancellationToken: cancellationToken);
    }

    [McpServerTool, Description("Gets expense records for one federal deputy, optionally filtered by year and month.")]
    public static Task<string> GetDeputadoDespesasAsync(
        [Description("Deputy id from Dados Abertos Câmara.")] int idDeputado,
        [Description("Expense year. Example: 2026.")] int? ano = null,
        [Description("Expense month from 1 to 12.")] int? mes = null,
        [Description("Page number. Starts at 1.")] int pagina = 1,
        [Description("Items per page. Recommended: 10 to 100.")] int itens = 20,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"deputados/{idDeputado}/despesas", new Dictionary<string, object?>
        {
            ["ano"] = ano,
            ["mes"] = mes,
            ["pagina"] = pagina,
            ["itens"] = itens,
            ["ordem"] = "ASC",
            ["ordenarPor"] = "dataDocumento"
        }, cancellationToken);
    }

    [McpServerTool, Description("Gets events related to one federal deputy.")]
    public static Task<string> GetDeputadoEventosAsync(
        [Description("Deputy id from Dados Abertos Câmara.")] int idDeputado,
        [Description("Start date in yyyy-MM-dd format.")] string? dataInicio = null,
        [Description("End date in yyyy-MM-dd format.")] string? dataFim = null,
        [Description("Page number. Starts at 1.")] int pagina = 1,
        [Description("Items per page. Recommended: 10 to 100.")] int itens = 20,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"deputados/{idDeputado}/eventos", new Dictionary<string, object?>
        {
            ["dataInicio"] = dataInicio,
            ["dataFim"] = dataFim,
            ["pagina"] = pagina,
            ["itens"] = itens,
            ["ordem"] = "ASC",
            ["ordenarPor"] = "dataHoraInicio"
        }, cancellationToken);
    }

    [McpServerTool, Description("Gets the party history for one federal deputy.")]
    public static Task<string> GetDeputadoHistoricoPartidosAsync(
        [Description("Deputy id from Dados Abertos Câmara.")] int idDeputado,
        CancellationToken cancellationToken = default)
    {
        return CamaraApiClient.GetJsonAsync($"deputados/{idDeputado}/historico", cancellationToken: cancellationToken);
    }
}
