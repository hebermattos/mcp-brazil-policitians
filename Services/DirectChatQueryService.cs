using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class DirectChatQueryService
{
    private const string DefaultCamaraBaseUrl = "https://dadosabertos.camara.leg.br/api/v2/";

    private static readonly Regex PropositionsByDeputyRegex = new(
        @"\b(?:proposi[cç][oõ]es|projetos?)\s+(?:d[ao]s?\s+)?(?:deputad[ao]|parlamentar)\s+(?<author>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExpensesByDeputyRegex = new(
        @"\b(?:despesas?|gastos?|cotas?|reembolsos?)\s+(?:d[ao]s?\s+)?(?:deputad[ao]|parlamentar)\s+(?<name>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DirectChatQueryService> _logger;

    public DirectChatQueryService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<DirectChatQueryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ChatPromptResponse?> TryHandleAsync(string prompt, CancellationToken cancellationToken)
    {
        var deputyNameForExpenses = TryExtractDeputyNameForExpenses(prompt);
        if (!string.IsNullOrWhiteSpace(deputyNameForExpenses))
        {
            return await GetDeputyExpensesAsync(deputyNameForExpenses, cancellationToken);
        }

        var author = TryExtractPropositionsAuthor(prompt);
        if (!string.IsNullOrWhiteSpace(author))
        {
            return await GetPropositionsByAuthorAsync(author, cancellationToken);
        }

        return null;
    }

    private async Task<ChatPromptResponse> GetPropositionsByAuthorAsync(string author, CancellationToken cancellationToken)
    {
        var page = GetInt("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", 1, 1, 10000);
        var items = GetInt("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", 10, 1, 100);
        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["autor"] = author,
            ["pagina"] = page.ToString(CultureInfo.InvariantCulture),
            ["itens"] = items.ToString(CultureInfo.InvariantCulture)
        };

        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["autor"] = author,
            ["pagina"] = page.ToString(CultureInfo.InvariantCulture),
            ["itens"] = items.ToString(CultureInfo.InvariantCulture)
        };

        var requestUri = BuildUri(GetString("CamaraApi:BaseUrl", "CAMARA_API_BASE_URL", DefaultCamaraBaseUrl), "proposicoes", query);

        _logger.LogInformation(
            "Handling direct chat query as propositions by author. Author={Author}, Url={Url}",
            author,
            requestUri);

        using var json = await GetJsonAsync(requestUri, "proposições por autor", cancellationToken);
        return new ChatPromptResponse(
            "Consulta direta de proposições por autor.",
            "search_proposicoes",
            arguments,
            json.RootElement.Clone(),
            null);
    }

    private async Task<ChatPromptResponse> GetDeputyExpensesAsync(string deputyName, CancellationToken cancellationToken)
    {
        var baseUrl = GetString("CamaraApi:BaseUrl", "CAMARA_API_BASE_URL", DefaultCamaraBaseUrl);
        var page = GetInt("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", 1, 1, 10000);
        var items = GetInt("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", 10, 1, 100);

        var searchDeputyUri = BuildUri(baseUrl, "deputados", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["nome"] = deputyName,
            ["pagina"] = "1",
            ["itens"] = "1"
        });

        _logger.LogInformation(
            "Handling direct chat query as deputy expenses. Step=search_deputados, DeputyName={DeputyName}, Url={Url}",
            deputyName,
            searchDeputyUri);

        using var deputySearchJson = await GetJsonAsync(searchDeputyUri, "busca de deputado para despesas", cancellationToken);
        var deputy = ExtractFirstDataItem(deputySearchJson.RootElement)
            ?? throw new InvalidOperationException($"Não encontrei deputado/deputada com o nome '{deputyName}'.");

        var deputyId = GetRequiredProperty(deputy.Value, "id");
        var resolvedDeputyName = GetOptionalProperty(deputy.Value, "nome") ?? deputyName;

        var expensesUri = BuildUri(baseUrl, $"deputados/{deputyId}/despesas", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["pagina"] = page.ToString(CultureInfo.InvariantCulture),
            ["itens"] = items.ToString(CultureInfo.InvariantCulture)
        });

        _logger.LogInformation(
            "Handling direct chat query as deputy expenses. Step=get_deputado_despesas, DeputyId={DeputyId}, DeputyName={DeputyName}, Url={Url}",
            deputyId,
            resolvedDeputyName,
            expensesUri);

        using var expensesJson = await GetJsonAsync(expensesUri, "despesas de deputado", cancellationToken);
        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["idDeputado"] = deputyId,
            ["nome"] = resolvedDeputyName,
            ["pagina"] = page.ToString(CultureInfo.InvariantCulture),
            ["itens"] = items.ToString(CultureInfo.InvariantCulture)
        };

        return new ChatPromptResponse(
            "Consulta direta de despesas por deputado.",
            "get_deputado_despesas",
            arguments,
            expensesJson.RootElement.Clone(),
            null);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri requestUri, string operationName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Camara API returned non-success status for direct query. Operation={Operation}, StatusCode={StatusCode}, Body={Body}",
                operationName,
                (int)response.StatusCode,
                Truncate(responseText));

            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao executar {operationName} na API da Câmara: {responseText}");
        }

        return JsonDocument.Parse(responseText);
    }

    private static string? TryExtractPropositionsAuthor(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var normalizedPrompt = Regex.Replace(prompt.Trim(), "\\s+", " ");
        var match = PropositionsByDeputyRegex.Match(normalizedPrompt);
        if (!match.Success)
        {
            return null;
        }

        return CleanPersonName(match.Groups["author"].Value);
    }

    private static string? TryExtractDeputyNameForExpenses(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var normalizedPrompt = Regex.Replace(prompt.Trim(), "\\s+", " ");
        var match = ExpensesByDeputyRegex.Match(normalizedPrompt);
        if (!match.Success)
        {
            return null;
        }

        return CleanPersonName(match.Groups["name"].Value);
    }

    private static string? CleanPersonName(string value)
    {
        var name = value.Trim();
        name = Regex.Replace(name, @"[?.!,;:]+$", string.Empty).Trim();
        name = Regex.Replace(name, @"\b(?:por favor|pfv|pra mim|para mim)$", string.Empty, RegexOptions.IgnoreCase).Trim();

        return string.IsNullOrWhiteSpace(name) ? null : ToTitleCase(name);
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

    private int GetInt(string configurationKey, string environmentKey, int defaultValue, int min, int max)
    {
        var rawValue = GetString(configurationKey, environmentKey, defaultValue.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
    }

    private static Uri BuildUri(string baseUrl, string relativePath, IReadOnlyDictionary<string, string?> query)
    {
        var builder = new UriBuilder(new Uri(new Uri(EnsureTrailingSlash(baseUrl)), relativePath));
        builder.Query = string.Join("&", query
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}"));

        return builder.Uri;
    }

    private static JsonElement? ExtractFirstDataItem(JsonElement root)
    {
        if (!root.TryGetProperty("dados", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return null;
        }

        return data[0].Clone();
    }

    private static string GetRequiredProperty(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"A resposta da API da Câmara não retornou o campo obrigatório '{propertyName}'.");
        }

        return property.ToString();
    }

    private static string? GetOptionalProperty(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var property) && property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? property.ToString()
            : null;
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private static string ToTitleCase(string value)
    {
        var textInfo = CultureInfo.GetCultureInfo("pt-BR").TextInfo;
        return textInfo.ToTitleCase(value.ToLower(CultureInfo.GetCultureInfo("pt-BR")));
    }

    private static string Truncate(string value)
    {
        const int maxChars = 2000;
        return value.Length <= maxChars ? value : value[..maxChars] + "\n... log truncado ...";
    }
}
