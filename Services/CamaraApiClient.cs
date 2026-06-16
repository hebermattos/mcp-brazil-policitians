using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace McpBrazilPoliticians.Services;

public static class CamaraApiClient
{
    private const string DefaultBaseUrl = "https://dadosabertos.camara.leg.br/api/v2/";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly string CacheDatabasePath = GetCacheDatabasePath();

    public static async Task<string> GetJsonAsync(
        string relativePath,
        IReadOnlyDictionary<string, object?>? query = null,
        CancellationToken cancellationToken = default)
    {
        var uri = BuildUri(relativePath, query);

        var cachedBody = await TryGetCachedResponseAsync(uri, cancellationToken).ConfigureAwait(false);
        if (cachedBody is not null)
        {
            return cachedBody;
        }

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

        var result = FormatJson(body);
        await SaveCachedResponseAsync(uri, result, cancellationToken).ConfigureAwait(false);

        return result;
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

    private static async Task<string?> TryGetCachedResponseAsync(string requestUri, CancellationToken cancellationToken)
    {
        await CacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheDatabaseAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = CreateCacheConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ResponseJson
                FROM ApiResponseCache
                WHERE CacheKey = $cacheKey
                  AND ExpiresUtc > $nowUtc
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$cacheKey", CreateCacheKey(requestUri));
            command.Parameters.AddWithValue("$nowUtc", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value as string;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static async Task SaveCachedResponseAsync(string requestUri, string responseJson, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(CacheDuration);

        await CacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCacheDatabaseAsync(cancellationToken).ConfigureAwait(false);

            await using var connection = CreateCacheConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using (var deleteExpiredCommand = connection.CreateCommand())
            {
                deleteExpiredCommand.CommandText = "DELETE FROM ApiResponseCache WHERE ExpiresUtc <= $nowUtc;";
                deleteExpiredCommand.Parameters.AddWithValue("$nowUtc", now.ToUnixTimeSeconds());
                await deleteExpiredCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ApiResponseCache (CacheKey, RequestUri, ResponseJson, CreatedUtc, ExpiresUtc)
                VALUES ($cacheKey, $requestUri, $responseJson, $createdUtc, $expiresUtc)
                ON CONFLICT(CacheKey) DO UPDATE SET
                    RequestUri = excluded.RequestUri,
                    ResponseJson = excluded.ResponseJson,
                    CreatedUtc = excluded.CreatedUtc,
                    ExpiresUtc = excluded.ExpiresUtc;
                """;
            command.Parameters.AddWithValue("$cacheKey", CreateCacheKey(requestUri));
            command.Parameters.AddWithValue("$requestUri", requestUri);
            command.Parameters.AddWithValue("$responseJson", responseJson);
            command.Parameters.AddWithValue("$createdUtc", now.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$expiresUtc", expires.ToUnixTimeSeconds());

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CacheLock.Release();
        }
    }

    private static async Task EnsureCacheDatabaseAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(CacheDatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = CreateCacheConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var walCommand = connection.CreateCommand())
        {
            walCommand.CommandText = "PRAGMA journal_mode = WAL;";
            await walCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ApiResponseCache (
                CacheKey TEXT PRIMARY KEY NOT NULL,
                RequestUri TEXT NOT NULL,
                ResponseJson TEXT NOT NULL,
                CreatedUtc INTEGER NOT NULL,
                ExpiresUtc INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ApiResponseCache_ExpiresUtc
            ON ApiResponseCache (ExpiresUtc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteConnection CreateCacheConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = CacheDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(builder.ToString());
    }

    private static string GetCacheDatabasePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("CAMARA_API_CACHE_SQLITE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(AppContext.BaseDirectory, "cache", "camara-api-cache.sqlite");
    }

    private static string CreateCacheKey(string requestUri)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(requestUri));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string FormatJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
        }
        catch (JsonException)
        {
            return value;
        }
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
