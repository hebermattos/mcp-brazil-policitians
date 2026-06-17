using System.Text.Json;

namespace McpBrazilPoliticians.Services;

public sealed class ProviderPromptLoggingHandler : DelegatingHandler
{
    private readonly ILogger<ProviderPromptLoggingHandler> _logger;
    private readonly IConfiguration _configuration;

    public ProviderPromptLoggingHandler(
        ILogger<ProviderPromptLoggingHandler> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (ShouldLogProviderPrompt(request) && request.Content is not null)
        {
            var requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            LogProviderPrompts(request, requestBody);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private void LogProviderPrompts(HttpRequestMessage request, string requestBody)
    {
        try
        {
            using var json = JsonDocument.Parse(requestBody);
            var root = json.RootElement;

            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    var role = message.TryGetProperty("role", out var roleElement)
                        ? roleElement.GetString()
                        : "unknown";

                    var content = message.TryGetProperty("content", out var contentElement)
                        ? ExtractContent(contentElement)
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    _logger.LogInformation(
                        """
                        ================= PROMPT ENVIADO AO PROVIDER =================
                        ProviderUrl={ProviderUrl}
                        Role={Role}
                        {Prompt}
                        ==============================================================
                        """,
                        request.RequestUri,
                        role,
                        TruncateForLog(content));
                }

                return;
            }

            if (root.TryGetProperty("prompt", out var promptElement))
            {
                var prompt = ExtractContent(promptElement);
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    _logger.LogInformation(
                        """
                        ================= PROMPT ENVIADO AO PROVIDER =================
                        ProviderUrl={ProviderUrl}
                        {Prompt}
                        ==============================================================
                        """,
                        request.RequestUri,
                        TruncateForLog(prompt));
                }

                return;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse provider request body to log prompts. ProviderUrl={ProviderUrl}", request.RequestUri);
        }

        _logger.LogInformation(
            """
            ================= REQUEST ENVIADO AO PROVIDER =================
            ProviderUrl={ProviderUrl}
            {RequestBody}
            ===============================================================
            """,
            request.RequestUri,
            TruncateForLog(requestBody));
    }

    private static bool ShouldLogProviderPrompt(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || request.Method != HttpMethod.Post)
        {
            return false;
        }

        return uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractContent(JsonElement contentElement)
    {
        return contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join("\n", contentElement.EnumerateArray().Select(ExtractContentFromArrayItem).Where(value => !string.IsNullOrWhiteSpace(value))),
            _ => contentElement.GetRawText()
        };
    }

    private static string ExtractContentFromArrayItem(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            return item.GetString() ?? string.Empty;
        }

        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var textElement))
        {
            return textElement.GetString() ?? textElement.GetRawText();
        }

        return item.GetRawText();
    }

    private string TruncateForLog(string value)
    {
        var maxChars = GetMaxLoggedBodyChars();
        return value.Length <= maxChars ? value : value[..maxChars] + "\n... log truncado ...";
    }

    private int GetMaxLoggedBodyChars()
    {
        var rawValue = _configuration["Logging:Chat:MaxLoggedBodyChars"]
            ?? Environment.GetEnvironmentVariable("LOG_CHAT_MAX_BODY_CHARS");

        return int.TryParse(rawValue, out var value)
            ? Math.Clamp(value, 100, 500000)
            : 10000;
    }
}
