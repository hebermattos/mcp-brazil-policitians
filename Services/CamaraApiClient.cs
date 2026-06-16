using System.Net;
using System.Text.Json;

namespace McpBrazilPoliticians.Services;

public static class CamaraApiClient
{
    private const string DefaultBaseUrl = "https://dadosabertos.camara.leg.br/api/v2/";

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static async Task<string> GetJsonAsync(
        string relativePath,
        IReadOnlyDictionary<string, object?>? query = null,
        CancellationToken cancellationToken = default)
    {
        var uri = BuildUri(relativePath, query);

        using var response = await HttpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return JsonSerializer.Serialize(new
            {
                error = true,
                statusCode = (int)response.StatusCode,
                reasonPhrase = response.ReasonPhrase,
                request = uri,
                response = TryParseJson(body) ?? body
            }, PrettyJsonOptions);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var baseUrl = Environment.GetEnvironmentVariable("CAMARA_API_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = DefaultBaseUrl;
        }

        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        var timeoutSeconds = 30;
        var timeoutValue = Environment.GetEnvironmentVariable("CAMARA_API_TIMEOUT_SECONDS");
        if (int.TryParse(timeoutValue, out var parsedTimeoutSeconds) && parsedTimeoutSeconds > 0)
        {
            timeoutSeconds = parsedTimeoutSeconds;
        }

        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mcp-brazil-policitians/1.0");
        return client;
    }

    private static string BuildUri(string relativePath, IReadOnlyDictionary<string, object?>? query)
    {
        var path = NormalizeRelativePath(relativePath);
        if (query is null || query.Count == 0)
        {
            return path;
        }

        var queryString = string.Join("&", query
            .Where(pair => pair.Value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(pair.Value)))
            .Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(Convert.ToString(pair.Value))}"));

        return string.IsNullOrWhiteSpace(queryString) ? path : $"{path}?{queryString}";
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("The Câmara API path is required.", nameof(relativePath));
        }

        if (Uri.TryCreate(relativePath, UriKind.Absolute, out _))
        {
            throw new ArgumentException("Use only relative API paths, for example 'deputados' or 'deputados/220593'.", nameof(relativePath));
        }

        var normalized = relativePath.Trim().TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path traversal is not allowed.", nameof(relativePath));
        }

        return normalized;
    }

    private static object? TryParseJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
