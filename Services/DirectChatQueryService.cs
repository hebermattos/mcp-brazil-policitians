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
        var author = TryExtractPropositionsAuthor(prompt);
        if (string.IsNullOrWhiteSpace(author))
        {
            return null;
        }

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

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Camara API returned non-success status for direct propositions by author query. StatusCode={StatusCode}, Body={Body}",
                (int)response.StatusCode,
                Truncate(responseText));

            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao consultar proposições por autor na API da Câmara: {responseText}");
        }

        using var json = JsonDocument.Parse(responseText);
        return new ChatPromptResponse(
            "Consulta direta de proposições por autor.",
            "search_proposicoes",
            arguments,
            json.RootElement.Clone(),
            null);
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

        var author = match.Groups["author"].Value.Trim();
        author = Regex.Replace(author, @"[?.!,;:]+$", string.Empty).Trim();
        author = Regex.Replace(author, @"\b(?:por favor|pfv|pra mim|para mim)$", string.Empty, RegexOptions.IgnoreCase).Trim();

        return string.IsNullOrWhiteSpace(author) ? null : ToTitleCase(author);
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
