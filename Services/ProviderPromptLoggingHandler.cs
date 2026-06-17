using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpBrazilPoliticians.Services;

public sealed class ProviderPromptLoggingHandler : DelegatingHandler
{
    private const string ToolPlannerPromptMarker = "Você é um planejador de ferramentas para a API de Dados Abertos da Câmara.";

    private const string ToolPlannerPromptEnhancement = """

Regras adicionais para listagens genéricas:
- Quando o usuário pedir apenas "listar proposições", "lista de proposições", "mostre proposições", "proposições recentes" ou algo genérico, use search_proposicoes sem keywords.
- Só use keywords quando o usuário informar um assunto real, tema, termo legislativo ou texto de busca específico.
- Nunca use como keywords frases genéricas de intenção, como "lista de proposições", "listar proposições", "mostrar proposições", "buscar proposições" ou apenas "proposições".
- Se o usuário pedir somente uma listagem genérica, use apenas pagina e itens.

Exemplos adicionais:
Usuário: lista de proposições
Resposta:
{
  "tool": "search_proposicoes",
  "arguments": {
    "pagina": 1,
    "itens": 10
  }
}

Usuário: proposições sobre escala 6x1
Resposta:
{
  "tool": "search_proposicoes",
  "arguments": {
    "keywords": "escala 6x1",
    "pagina": 1,
    "itens": 10
  }
}

Usuário: deputados do RS
Resposta:
{
  "tool": "search_deputados",
  "arguments": {
    "siglaUf": "RS",
    "pagina": 1,
    "itens": 10
  }
}
""";

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
            requestBody = EnhanceProviderRequestBody(request, requestBody);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            LogProviderPrompts(request, requestBody);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private string EnhanceProviderRequestBody(HttpRequestMessage request, string requestBody)
    {
        try
        {
            var root = JsonNode.Parse(requestBody) as JsonObject;
            var messages = root?["messages"] as JsonArray;
            if (messages is null)
            {
                return requestBody;
            }

            var changed = false;
            foreach (var message in messages.OfType<JsonObject>())
            {
                var role = message["role"]?.GetValue<string>();
                if (!string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var content = message["content"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(content)
                    || !content.Contains(ToolPlannerPromptMarker, StringComparison.OrdinalIgnoreCase)
                    || content.Contains("Regras adicionais para listagens genéricas", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                message["content"] = content + ToolPlannerPromptEnhancement;
                changed = true;
            }

            if (!changed)
            {
                return requestBody;
            }

            _logger.LogInformation("Provider tool planner prompt enhanced for generic list requests. ProviderUrl={ProviderUrl}", request.RequestUri);
            return root!.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not enhance provider request body. ProviderUrl={ProviderUrl}", request.RequestUri);
            return requestBody;
        }
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
