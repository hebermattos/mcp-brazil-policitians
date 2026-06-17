using System.Globalization;
using System.Text.Json;

namespace McpBrazilPoliticians.Services;

public sealed class CamaraToolExecutionService
{
    private const string DefaultCamaraBaseUrl = "https://dadosabertos.camara.leg.br/api/v2/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CamaraToolExecutionService> _logger;

    public CamaraToolExecutionService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CamaraToolExecutionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<JsonDocument> ExecuteAsync(
        string tool,
        IReadOnlyDictionary<string, string?> arguments,
        CancellationToken cancellationToken)
    {
        var (path, query) = BuildRequest(tool, arguments);
        var requestUri = BuildUri(GetString("CamaraApi:BaseUrl", "CAMARA_API_BASE_URL", DefaultCamaraBaseUrl), path, query);

        _logger.LogInformation(
            "Executing Camara tool. Tool={Tool}, Path={Path}, Query={Query}, Url={Url}",
            tool,
            path,
            JsonSerializer.Serialize(query),
            requestUri);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Camara tool returned non-success status. Tool={Tool}, StatusCode={StatusCode}, Body={Body}",
                tool,
                (int)response.StatusCode,
                Truncate(responseText));

            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao executar a ferramenta '{tool}' na API da Câmara: {responseText}");
        }

        return JsonDocument.Parse(responseText);
    }

    private static (string Path, IReadOnlyDictionary<string, string?> Query) BuildRequest(
        string tool,
        IReadOnlyDictionary<string, string?> arguments)
    {
        var normalizedTool = tool.Trim().ToLowerInvariant();
        return normalizedTool switch
        {
            "search_deputados" => ("deputados", WithoutEmptyValues(arguments)),
            "get_deputado" => ($"deputados/{Required(arguments, "idDeputado")}", EmptyQuery()),
            "get_deputado_despesas" => ($"deputados/{Required(arguments, "idDeputado")}/despesas", RemoveIdentifier(arguments, "idDeputado")),
            "search_proposicoes" => ("proposicoes", WithoutEmptyValues(arguments)),
            "get_proposicao" => ($"proposicoes/{Required(arguments, "idProposicao")}", EmptyQuery()),
            "search_eventos" => ("eventos", WithoutEmptyValues(arguments)),
            "search_orgaos" => ("orgaos", WithoutEmptyValues(arguments)),
            _ => throw new InvalidOperationException($"Ferramenta não suportada pelo executor genérico: {tool}")
        };
    }

    private string GetString(string configurationKey, string environmentKey, string defaultValue)
    {
        var configurationValue = _configuration[configurationKey];
        if (!string.IsNullOrWhiteSpace(configurationValue))
        {
            return configurationValue;
        }

        var environmentValue = Environment.GetEnvironmentVariable(environmentKey);
        return string.IsNullOrWhiteSpace(environmentValue) ? defaultValue : environmentValue;
    }

    private static Uri BuildUri(string baseUrl, string relativePath, IReadOnlyDictionary<string, string?> query)
    {
        var builder = new UriBuilder(new Uri(new Uri(EnsureTrailingSlash(baseUrl)), relativePath));
        builder.Query = string.Join("&", query
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}"));

        return builder.Uri;
    }

    private static string Required(IReadOnlyDictionary<string, string?> arguments, string key)
    {
        return arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"O argumento obrigatório '{key}' não foi informado.");
    }

    private static IReadOnlyDictionary<string, string?> RemoveIdentifier(IReadOnlyDictionary<string, string?> arguments, string key)
    {
        return arguments
            .Where(item => !item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> WithoutEmptyValues(IReadOnlyDictionary<string, string?> arguments)
    {
        return arguments
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> EmptyQuery() => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private static string Truncate(string value)
    {
        const int maxChars = 2000;
        return value.Length <= maxChars ? value : value[..maxChars] + "\n... log truncado ...";
    }
}
