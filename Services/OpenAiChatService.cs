using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class OpenAiChatService
{
    private const string DefaultChatProvider = "ollama";
    private const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1/";
    private const string DefaultOpenAiModel = "gpt-4.1-mini";
    private const int DefaultOpenAiTimeoutSeconds = 60;
    private const string DefaultOllamaModel = "llama3.1:8b";
    private const string DefaultOllamaBaseUrl = "http://localhost:11434";
    private const int DefaultOllamaTimeoutSeconds = 120;
    private const string DefaultCamaraBaseUrl = "https://dadosabertos.camara.leg.br/api/v2/";
    private const int DefaultCamaraTimeoutSeconds = 30;
    private const int DefaultSearchItems = 10;
    private const int DefaultSearchPage = 1;
    private const int DefaultMaxDataJsonChars = 20_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiChatService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public OpenAiChatService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OpenAiChatService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ChatPromptResponse> GetAnswerAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var plan = await CreateToolPlanAsync(prompt, cancellationToken);
        using var data = await ExecuteCamaraToolAsync(plan, cancellationToken);
        var answer = await CreateFinalAnswerAsync(prompt, plan, data, cancellationToken);

        return new ChatPromptResponse(
            answer,
            plan.Tool,
            plan.Arguments.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.OrdinalIgnoreCase),
            data.RootElement.Clone());
    }

    private async Task<ToolPlan> CreateToolPlanAsync(string prompt, CancellationToken cancellationToken)
    {
        var defaultItems = GetIntSetting("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", DefaultSearchItems, minValue: 1, maxValue: 100);
        var defaultPage = GetIntSetting("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", DefaultSearchPage, minValue: 1, maxValue: 10_000);

        var systemPrompt = $$"""
Você é um planejador de ferramentas para um sistema que consulta a API de Dados Abertos da Câmara dos Deputados.
Responda exclusivamente com JSON válido, sem markdown.

Escolha uma ferramenta:
- search_deputados: buscar deputados. Argumentos: nome, siglaUf, siglaPartido, idLegislatura, pagina, itens.
- get_deputado: detalhe de deputado por idDeputado.
- search_proposicoes: buscar proposições. Argumentos: siglaTipo, numero, ano, ementa, autor, dataInicio, dataFim, pagina, itens.
- get_proposicao: detalhe de proposição por idProposicao.
- search_eventos: buscar eventos. Argumentos: dataInicio, dataFim, descricao, pagina, itens.
- search_orgaos: buscar órgãos/comissões. Argumentos: sigla, nome, pagina, itens.

Regras:
- Para perguntas sobre projetos de lei, PEC, MP, proposições, assuntos legislativos ou temas como escala 6x1, use search_proposicoes.
- Para perguntas sobre deputados/parlamentares, use search_deputados, exceto quando houver id e o usuário pedir detalhes.
- Use itens no máximo {{defaultItems}}.
- Use pagina {{defaultPage}} quando o usuário não informar página.
- Quando o usuário informar UF, use siglaUf com duas letras maiúsculas.
- Quando o usuário informar ano, use ano numérico.
- Quando buscar por assunto de proposição, coloque o assunto principal em ementa.

Formato obrigatório:
{
  "tool": "search_proposicoes",
  "arguments": {
    "ementa": "escala 6x1",
    "itens": {{defaultItems}},
    "pagina": {{defaultPage}}
  }
}
""";

        var content = await CallChatModelAsync(systemPrompt, prompt, forceJson: true, cancellationToken);

        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            var tool = root.GetProperty("tool").GetString();

            if (string.IsNullOrWhiteSpace(tool))
            {
                throw new InvalidOperationException("O modelo não retornou a ferramenta a ser chamada.");
            }

            var arguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in argsElement.EnumerateObject())
                {
                    var value = ConvertJsonElementToString(property.Value);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        arguments[property.Name] = value;
                    }
                }
            }

            if (tool.StartsWith("search_", StringComparison.OrdinalIgnoreCase))
            {
                arguments.TryAdd("itens", defaultItems.ToString());
                arguments.TryAdd("pagina", defaultPage.ToString());
            }

            return new ToolPlan(tool, arguments);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Chat model returned an invalid tool plan: {Content}", content);
            throw new InvalidOperationException("Não foi possível interpretar o plano de ferramenta retornado pelo modelo.", ex);
        }
    }

    private async Task<JsonDocument> ExecuteCamaraToolAsync(ToolPlan plan, CancellationToken cancellationToken)
    {
        var (path, query) = BuildCamaraRequest(plan);
        var baseUrl = GetStringSetting("CamaraApi:BaseUrl", "CAMARA_API_BASE_URL", DefaultCamaraBaseUrl);
        var timeoutSeconds = GetIntSetting("CamaraApi:TimeoutSeconds", "CAMARA_API_TIMEOUT_SECONDS", DefaultCamaraTimeoutSeconds, minValue: 1, maxValue: 300);
        var requestUri = BuildUri(baseUrl, path, query);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, timeoutSeconds, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao consultar a API da Câmara: {responseText}");
        }

        return JsonDocument.Parse(responseText);
    }

    private static (string Path, IReadOnlyDictionary<string, string?> Query) BuildCamaraRequest(ToolPlan plan)
    {
        var args = plan.Arguments;
        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in args)
        {
            if (!IsIdentifierArgument(item.Key))
            {
                query[item.Key] = item.Value;
            }
        }

        return plan.Tool.ToLowerInvariant() switch
        {
            "search_deputados" => ("deputados", query),
            "get_deputado" => ($"deputados/{Required(args, "idDeputado")}", EmptyQuery()),
            "search_proposicoes" => ("proposicoes", query),
            "get_proposicao" => ($"proposicoes/{Required(args, "idProposicao")}", EmptyQuery()),
            "search_eventos" => ("eventos", query),
            "search_orgaos" => ("orgaos", query),
            _ => throw new InvalidOperationException($"Ferramenta não suportada pelo backend de chat: {plan.Tool}")
        };
    }

    private async Task<string> CreateFinalAnswerAsync(
        string prompt,
        ToolPlan plan,
        JsonDocument data,
        CancellationToken cancellationToken)
    {
        var maxDataJsonChars = GetIntSetting("Chat:MaxDataJsonChars", "CHAT_MAX_DATA_JSON_CHARS", DefaultMaxDataJsonChars, minValue: 1_000, maxValue: 500_000);
        var dataJson = data.RootElement.GetRawText();

        if (dataJson.Length > maxDataJsonChars)
        {
            dataJson = dataJson[..maxDataJsonChars] + "\n... conteúdo truncado ...";
        }

        var systemPrompt = """
Você é um assistente que responde em português do Brasil usando dados públicos da Câmara dos Deputados.
Use somente os dados fornecidos no JSON. Não invente nomes, IDs, partidos, datas ou resultados.
Quando houver lista de itens, resuma os mais relevantes em formato legível.
Quando não houver resultado, diga que não encontrou resultados com os filtros usados.
Se útil, informe qual consulta foi executada.
""";

        var userContent = $"""
Pergunta do usuário:
{prompt}

Ferramenta executada:
{plan.Tool}

Argumentos:
{JsonSerializer.Serialize(plan.Arguments, _jsonOptions)}

JSON retornado pela API da Câmara:
{dataJson}
""";

        return await CallChatModelAsync(systemPrompt, userContent, forceJson: false, cancellationToken);
    }

    private Task<string> CallChatModelAsync(
        string systemPrompt,
        string userPrompt,
        bool forceJson,
        CancellationToken cancellationToken)
    {
        var provider = GetChatProvider();

        return provider switch
        {
            "openai" => CallOpenAiChatAsync(systemPrompt, userPrompt, forceJson, cancellationToken),
            "ollama" => CallOllamaChatAsync(systemPrompt, userPrompt, forceJson, cancellationToken),
            _ => throw new InvalidOperationException($"Provedor de chat inválido: '{provider}'. Use 'openai' ou 'ollama'.")
        };
    }

    private async Task<string> CallOpenAiChatAsync(
        string systemPrompt,
        string userPrompt,
        bool forceJson,
        CancellationToken cancellationToken)
    {
        var apiKey = GetStringSetting("Chat:OpenAI:ApiKey", "OPENAI_API_KEY", string.Empty);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Configure Chat:OpenAI:ApiKey no appsettings.json antes de usar o provedor OpenAI.");
        }

        var baseUrl = GetStringSetting("Chat:OpenAI:BaseUrl", "OPENAI_BASE_URL", DefaultOpenAiBaseUrl);
        var model = GetStringSetting("Chat:OpenAI:Model", "OPENAI_MODEL", DefaultOpenAiModel);
        var timeoutSeconds = GetIntSetting("Chat:OpenAI:TimeoutSeconds", "OPENAI_TIMEOUT_SECONDS", DefaultOpenAiTimeoutSeconds, minValue: 1, maxValue: 300);
        var endpoint = BuildOpenAiEndpoint(baseUrl);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        if (forceJson)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, timeoutSeconds, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao chamar a OpenAI: {responseText}");
        }

        using var json = JsonDocument.Parse(responseText);
        var content = json.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("A OpenAI retornou uma resposta vazia.");
        }

        return content;
    }

    private async Task<string> CallOllamaChatAsync(
        string systemPrompt,
        string userPrompt,
        bool forceJson,
        CancellationToken cancellationToken)
    {
        var baseUrl = GetStringSetting("Chat:Ollama:BaseUrl", "OLLAMA_BASE_URL", DefaultOllamaBaseUrl);
        var model = GetStringSetting("Chat:Ollama:Model", "OLLAMA_MODEL", DefaultOllamaModel);
        var timeoutSeconds = GetIntSetting("Chat:Ollama:TimeoutSeconds", "OLLAMA_TIMEOUT_SECONDS", DefaultOllamaTimeoutSeconds, minValue: 1, maxValue: 600);
        var useJsonFormat = GetBoolSetting("Chat:Ollama:UseJsonFormat", "OLLAMA_USE_JSON_FORMAT", defaultValue: true);
        var endpoint = BuildOllamaEndpoint(baseUrl);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        if (forceJson && useJsonFormat)
        {
            payload["format"] = "json";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, timeoutSeconds, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao chamar o Ollama: {responseText}");
        }

        using var json = JsonDocument.Parse(responseText);
        var root = json.RootElement;
        string? content = null;

        if (root.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.Object
            && messageElement.TryGetProperty("content", out var contentElement))
        {
            content = contentElement.GetString();
        }
        else if (root.TryGetProperty("response", out var responseElement))
        {
            content = responseElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("O Ollama retornou uma resposta vazia.");
        }

        return content;
    }

    private string GetChatProvider()
    {
        return GetStringSetting("Chat:Provider", "CHAT_PROVIDER", DefaultChatProvider)
            .Trim()
            .ToLowerInvariant();
    }

    private string GetStringSetting(string configurationKey, string environmentKey, string defaultValue)
    {
        var configurationValue = _configuration[configurationKey];
        if (!string.IsNullOrWhiteSpace(configurationValue))
        {
            return configurationValue;
        }

        var environmentValue = Environment.GetEnvironmentVariable(environmentKey);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return defaultValue;
    }

    private int GetIntSetting(string configurationKey, string environmentKey, int defaultValue, int minValue, int maxValue)
    {
        var rawValue = GetStringSetting(configurationKey, environmentKey, defaultValue.ToString());
        if (!int.TryParse(rawValue, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, minValue, maxValue);
    }

    private bool GetBoolSetting(string configurationKey, string environmentKey, bool defaultValue)
    {
        var rawValue = GetStringSetting(configurationKey, environmentKey, defaultValue.ToString());
        return bool.TryParse(rawValue, out var value) ? value : defaultValue;
    }

    private static async Task<HttpResponseMessage> SendAsyncWithTimeout(
        HttpClient httpClient,
        HttpRequestMessage request,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return await httpClient.SendAsync(request, timeoutCts.Token);
    }

    private static Uri BuildOpenAiEndpoint(string baseUrl)
    {
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return new Uri(new Uri(baseUrl), "chat/completions");
    }

    private static Uri BuildOllamaEndpoint(string baseUrl)
    {
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return new Uri(new Uri(baseUrl), "api/chat");
    }

    private static Uri BuildUri(string baseUrl, string relativePath, IReadOnlyDictionary<string, string?> query)
    {
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        var builder = new UriBuilder(new Uri(new Uri(baseUrl), relativePath));
        var encodedQuery = string.Join(
            "&",
            query
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));

        builder.Query = encodedQuery;
        return builder.Uri;
    }

    private static string Required(IReadOnlyDictionary<string, string?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"O argumento obrigatório '{key}' não foi informado.");
        }

        return value;
    }

    private static bool IsIdentifierArgument(string key)
    {
        return key.Equals("idDeputado", StringComparison.OrdinalIgnoreCase)
            || key.Equals("idProposicao", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> EmptyQuery()
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ConvertJsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private sealed record ToolPlan(string Tool, IReadOnlyDictionary<string, string?> Arguments);
}
