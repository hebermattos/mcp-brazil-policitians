using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace McpBrazilPoliticians.Services;

public sealed class CamaraApiClient
{
    private const string DefaultBaseUrl = "https://dadosabertos.camara.leg.br/api/v2/";
    private const string DefaultCachePath = "data/camara-cache.sqlite";
    private const int DefaultTimeoutSeconds = 30;
    private const int DefaultCacheTtlMinutes = 60;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CamaraApiClient> _logger;
    private readonly SemaphoreSlim _cacheInitLock = new(1, 1);
    private bool _cacheInitialized;

    public CamaraApiClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CamaraApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<JsonDocument> GetJsonAsync(
        string relativePath,
        IReadOnlyDictionary<string, string?> query,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildUri(GetBaseUrl(), relativePath, query);
        var cacheTtl = GetCacheTtl();
        var cachePath = GetCachePath();

        if (cacheTtl > TimeSpan.Zero)
        {
            var cachedResponse = await TryReadCacheAsync(cachePath, requestUri, cancellationToken);
            if (!string.IsNullOrWhiteSpace(cachedResponse))
            {
                _logger.LogInformation("Camara API cache hit. Url={Url}", requestUri);
                return JsonDocument.Parse(cachedResponse);
            }
        }

        _logger.LogInformation("Camara API cache miss. Url={Url}", requestUri);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.ParseAdd("application/json");

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, GetTimeoutSeconds(), cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Camara API returned non-success status. StatusCode={StatusCode}, Url={Url}, Body={Body}",
                (int)response.StatusCode,
                requestUri,
                Truncate(responseText));

            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao consultar a API da Câmara: {responseText}");
        }

        if (cacheTtl > TimeSpan.Zero)
        {
            await WriteCacheAsync(cachePath, requestUri, responseText, cacheTtl, cancellationToken);
        }

        return JsonDocument.Parse(responseText);
    }

    private async Task<string?> TryReadCacheAsync(string cachePath, Uri requestUri, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureCacheAsync(cachePath, cancellationToken);

            await using var connection = new SqliteConnection(CreateConnectionString(cachePath));
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT response_text, expires_at_utc
                FROM api_cache
                WHERE cache_key = $cacheKey
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$cacheKey", CreateCacheKey(requestUri));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var responseText = reader.GetString(0);
            var expiresAtRaw = reader.GetString(1);
            if (DateTimeOffset.TryParse(expiresAtRaw, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow)
            {
                return responseText;
            }

            await DeleteCacheEntryAsync(connection, requestUri, cancellationToken);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Camara API cache. Url={Url}", requestUri);
            return null;
        }
    }

    private async Task WriteCacheAsync(string cachePath, Uri requestUri, string responseText, TimeSpan ttl, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureCacheAsync(cachePath, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var expiresAt = now.Add(ttl);

            await using var connection = new SqliteConnection(CreateConnectionString(cachePath));
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO api_cache(cache_key, url, response_text, created_at_utc, expires_at_utc)
                VALUES($cacheKey, $url, $responseText, $createdAtUtc, $expiresAtUtc)
                ON CONFLICT(cache_key) DO UPDATE SET
                    url = excluded.url,
                    response_text = excluded.response_text,
                    created_at_utc = excluded.created_at_utc,
                    expires_at_utc = excluded.expires_at_utc;
                """;
            command.Parameters.AddWithValue("$cacheKey", CreateCacheKey(requestUri));
            command.Parameters.AddWithValue("$url", requestUri.ToString());
            command.Parameters.AddWithValue("$responseText", responseText);
            command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
            command.Parameters.AddWithValue("$expiresAtUtc", expiresAt.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write Camara API cache. Url={Url}", requestUri);
        }
    }

    private async Task DeleteCacheEntryAsync(SqliteConnection connection, Uri requestUri, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM api_cache WHERE cache_key = $cacheKey;";
        command.Parameters.AddWithValue("$cacheKey", CreateCacheKey(requestUri));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureCacheAsync(string cachePath, CancellationToken cancellationToken)
    {
        if (_cacheInitialized)
        {
            return;
        }

        await _cacheInitLock.WaitAsync(cancellationToken);
        try
        {
            if (_cacheInitialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = new SqliteConnection(CreateConnectionString(cachePath));
            await connection.OpenAsync(cancellationToken);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                await pragma.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS api_cache (
                    cache_key TEXT PRIMARY KEY,
                    url TEXT NOT NULL,
                    response_text TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_api_cache_expires_at_utc
                ON api_cache(expires_at_utc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            _cacheInitialized = true;
        }
        finally
        {
            _cacheInitLock.Release();
        }
    }

    private string GetBaseUrl() => GetString("CamaraApi:BaseUrl", "CAMARA_API_BASE_URL", DefaultBaseUrl);

    private int GetTimeoutSeconds() => GetInt("CamaraApi:TimeoutSeconds", "CAMARA_API_TIMEOUT_SECONDS", DefaultTimeoutSeconds, 1, 300);

    private TimeSpan GetCacheTtl()
    {
        var minutes = GetInt("CamaraApi:CacheTtlMinutes", "CAMARA_API_CACHE_TTL_MINUTES", DefaultCacheTtlMinutes, 0, 1440);
        return minutes <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(minutes);
    }

    private string GetCachePath() => GetString("CamaraApi:CacheSqlitePath", "CAMARA_API_CACHE_SQLITE_PATH", DefaultCachePath);

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
        var rawValue = GetString(configurationKey, environmentKey, defaultValue.ToString());
        return int.TryParse(rawValue, out var value) ? Math.Clamp(value, min, max) : defaultValue;
    }

    private static Uri BuildUri(string baseUrl, string relativePath, IReadOnlyDictionary<string, string?> query)
    {
        var builder = new UriBuilder(new Uri(new Uri(EnsureTrailingSlash(baseUrl)), relativePath));
        builder.Query = string.Join("&", query
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}"));

        return builder.Uri;
    }

    private static string CreateConnectionString(string cachePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = cachePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return builder.ToString();
    }

    private static string CreateCacheKey(Uri requestUri)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(requestUri.ToString()));
        return Convert.ToHexString(bytes);
    }

    private static async Task<HttpResponseMessage> SendAsyncWithTimeout(HttpClient httpClient, HttpRequestMessage request, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return await httpClient.SendAsync(request, timeoutCts.Token);
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private static string Truncate(string value)
    {
        const int maxChars = 2000;
        return value.Length <= maxChars ? value : value[..maxChars] + "\n... log truncado ...";
    }
}
