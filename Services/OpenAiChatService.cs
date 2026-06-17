using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class OpenAiChatService
{
    private const string DefaultProvider = "ollama";
    private const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1/";
    private const string DefaultOpenAiModel = "gpt-4.1-mini";
    private const string DefaultOllamaBaseUrl = "http://localhost:11434";
    private const string DefaultOllamaModel = "llama3.1:8b";
    private const string DefaultCamaraBaseUrl = "https://dadosabertos.camara.leg.br/api/v2/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiChatService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public OpenAiChatService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<OpenAiChatService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ChatPromptResponse> GetAnswerAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var operationId = Guid.NewGuid().ToString("N");
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["ChatOperationId"] = operationId
        });

        var stopwatch = Stopwatch.StartNew();
        var provider = GetProvider();

        _logger.LogInformation(
            "Chat request started. Provider={Provider}, PromptLength={PromptLength}, Prompt={Prompt}",
            provider,
            prompt.Length,
            GetOptionalLogText("Logging:Chat:IncludePromptContent", "LOG_CHAT_INCLUDE_PROMPT_CONTENT", prompt));

        try
        {
            var plan = await CreateToolPlanAsync(prompt, cancellationToken);
            using var data = await ExecuteCamaraToolAsync(plan, cancellationToken);
            var answer = await CreateFinalAnswerAsync(prompt, plan, data, cancellationToken);

            stopwatch.Stop();
            _logger.LogInformation(
                "Chat request completed. Provider={Provider}, Tool={Tool}, Arguments={Arguments}, ElapsedMs={ElapsedMs}",
                provider,
                plan.Tool,
                JsonSerializer.Serialize(plan.Arguments, _jsonOptions),
                stopwatch.ElapsedMilliseconds);

            return new ChatPromptResponse(
                answer,
                plan.Tool,
                plan.Arguments.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.OrdinalIgnoreCase),
                data.RootElement.Clone());
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Chat request failed. Provider={Provider}, ElapsedMs={ElapsedMs}", provider, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private async Task<ToolPlan> CreateToolPlanAsync(string prompt, CancellationToken cancellationToken)
    {
        var defaultItems = GetInt("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", 10, 1, 100);
        var defaultPage = GetInt("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", 1, 1, 10000);

        var systemPrompt = $$"""
Você é um planejador de ferramentas para a API de Dados Abertos da Câmara.
Responda apenas JSON válido, sem markdown.

Ferramentas permitidas e argumentos permitidos:
- search_deputados: nome, siglaUf, siglaPartido, idLegislatura, pagina, itens.
- get_deputado: idDeputado.
- search_proposicoes: siglaTipo, numero, ano, ementa, autor, dataInicio, dataFim, pagina, itens.
- get_proposicao: idProposicao.
- search_eventos: dataInicio, dataFim, descricao, pagina, itens.
- search_orgaos: sigla, nome, pagina, itens.

Regras:
- Para deputados, parlamentares, partido ou deputados por partido, use search_deputados.
- Para projetos de lei, PEC, MP, proposições ou temas legislativos, use search_proposicoes.
- Use pagina {{defaultPage}} e itens no máximo {{defaultItems}} quando o usuário não informar.
- Para UF use siglaUf. Para partido use siglaPartido.
- Nunca crie nomes de ferramentas ou argumentos fora da lista permitida.

Formato:
{
  "tool": "search_deputados",
  "arguments": {
    "siglaUf": "RS",
    "pagina": {{defaultPage}},
    "itens": {{defaultItems}}
  }
}
""";

        _logger.LogInformation(
            "Creating tool plan. Provider={Provider}, ForceJson=true, DefaultPage={DefaultPage}, DefaultItems={DefaultItems}",
            GetProvider(),
            defaultPage,
            defaultItems);

        var content = await CallChatModelAsync(systemPrompt, prompt, forceJson: true, cancellationToken);

        _logger.LogInformation(
            "Raw tool plan returned by model. Content={Content}",
            GetOptionalLogText("Logging:Chat:IncludeModelResponses", "LOG_CHAT_INCLUDE_MODEL_RESPONSES", content));

        try
        {
            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;
            var rawTool = root.GetProperty("tool").GetString();
            var tool = NormalizeToolName(rawTool);

            if (string.IsNullOrWhiteSpace(tool))
            {
                throw new InvalidOperationException("O modelo não retornou a ferramenta a ser chamada.");
            }

            var rawArgs = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in argsElement.EnumerateObject())
                {
                    var value = ConvertJsonElementToString(property.Value);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        rawArgs[property.Name] = value;
                    }
                }
            }

            if (tool.StartsWith("search_", StringComparison.OrdinalIgnoreCase))
            {
                rawArgs.TryAdd("pagina", defaultPage.ToString());
                rawArgs.TryAdd("itens", defaultItems.ToString());
            }

            var normalizedArgs = NormalizeArguments(tool, rawArgs);

            _logger.LogInformation(
                "Tool plan normalized. RawTool={RawTool}, Tool={Tool}, RawArguments={RawArguments}, NormalizedArguments={NormalizedArguments}",
                rawTool,
                tool,
                JsonSerializer.Serialize(rawArgs, _jsonOptions),
                JsonSerializer.Serialize(normalizedArgs, _jsonOptions));

            return new ToolPlan(tool, normalizedArgs);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Invalid tool plan returned by model. Content={Content}", content);
            throw new InvalidOperationException("Não foi possível interpretar o plano de ferramenta retornado pelo modelo.", ex);
        }
    }

    private async Task<JsonDocument> ExecuteCamaraToolAsync(ToolPlan plan, CancellationToken cancellationToken)
    {
        var (path, query) = BuildCamaraRequest(plan);
        var baseUrl = GetString("CamaraApi:BaseUrl", "CAMARA_API_BASE_URL", DefaultCamaraBaseUrl);
        var timeoutSeconds = GetInt("CamaraApi:TimeoutSeconds", "CAMARA_API_TIMEOUT_SECONDS", 30, 1, 300);
        var requestUri = BuildUri(baseUrl, path, query);

        _logger.LogInformation(
            "Calling Camara API. Tool={Tool}, Path={Path}, Query={Query}, Url={Url}, TimeoutSeconds={TimeoutSeconds}",
            plan.Tool,
            path,
            JsonSerializer.Serialize(query, _jsonOptions),
            requestUri,
            timeoutSeconds);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var stopwatch = Stopwatch.StartNew();
        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, timeoutSeconds, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        _logger.LogInformation(
            "Camara API response received. StatusCode={StatusCode}, ElapsedMs={ElapsedMs}, BodyLength={BodyLength}, Body={Body}",
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            responseText.Length,
            GetOptionalLogText("Logging:Chat:IncludeCamaraResponseBody", "LOG_CHAT_INCLUDE_CAMARA_RESPONSE_BODY", responseText));

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Camara API returned non-success status. StatusCode={StatusCode}, Url={Url}, Body={Body}",
                (int)response.StatusCode,
                requestUri,
                TruncateForLog(responseText));

            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao consultar a API da Câmara: {responseText}");
        }

        return JsonDocument.Parse(responseText);
    }

    private static (string Path, IReadOnlyDictionary<string, string?> Query) BuildCamaraRequest(ToolPlan plan)
    {
        var args = NormalizeArguments(plan.Tool, plan.Arguments);
        var query = args
            .Where(x => !IsIdentifierArgument(x.Key))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

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

    private async Task<string> CreateFinalAnswerAsync(string prompt, ToolPlan plan, JsonDocument data, CancellationToken cancellationToken)
    {
        var maxChars = GetInt("Chat:MaxDataJsonChars", "CHAT_MAX_DATA_JSON_CHARS", 20000, 1000, 500000);
        var dataJson = data.RootElement.GetRawText();
        if (dataJson.Length > maxChars)
        {
            dataJson = dataJson[..maxChars] + "\n... conteúdo truncado ...";
        }

        var systemPrompt = """
Você responde em português do Brasil usando somente o JSON fornecido da Câmara dos Deputados.
Não invente dados. Se houver lista, resuma de forma legível.
Se o usuário pedir agrupamento, agrupe apenas os itens retornados.
Se não houver resultado, diga que não encontrou resultados com os filtros usados.
""";

        var userContent = $"""
Pergunta do usuário:
{prompt}

Ferramenta executada:
{plan.Tool}

Argumentos enviados:
{JsonSerializer.Serialize(plan.Arguments, _jsonOptions)}

JSON retornado pela API da Câmara:
{dataJson}
""";

        _logger.LogInformation(
            "Creating final answer. Tool={Tool}, DataJsonLength={DataJsonLength}, MaxDataJsonChars={MaxDataJsonChars}",
            plan.Tool,
            dataJson.Length,
            maxChars);

        var answer = await CallChatModelAsync(systemPrompt, userContent, forceJson: false, cancellationToken);

        _logger.LogInformation(
            "Final answer generated. AnswerLength={AnswerLength}, Answer={Answer}",
            answer.Length,
            GetOptionalLogText("Logging:Chat:IncludeModelResponses", "LOG_CHAT_INCLUDE_MODEL_RESPONSES", answer));

        return answer;
    }

    private Task<string> CallChatModelAsync(string systemPrompt, string userPrompt, bool forceJson, CancellationToken cancellationToken)
    {
        return GetProvider() switch
        {
            "openai" => CallOpenAiChatAsync(systemPrompt, userPrompt, forceJson, cancellationToken),
            "ollama" => CallOllamaChatAsync(systemPrompt, userPrompt, forceJson, cancellationToken),
            var provider => throw new InvalidOperationException($"Provedor de chat inválido: '{provider}'. Use 'openai' ou 'ollama'.")
        };
    }

    private async Task<string> CallOpenAiChatAsync(string systemPrompt, string userPrompt, bool forceJson, CancellationToken cancellationToken)
    {
        var apiKey = GetString("Chat:OpenAI:ApiKey", "OPENAI_API_KEY", string.Empty);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Configure Chat:OpenAI:ApiKey no appsettings.json antes de usar OpenAI.");
        }

        var model = GetString("Chat:OpenAI:Model", "OPENAI_MODEL", DefaultOpenAiModel);
        var endpoint = BuildOpenAiEndpoint(GetString("Chat:OpenAI:BaseUrl", "OPENAI_BASE_URL", DefaultOpenAiBaseUrl));
        var timeoutSeconds = GetInt("Chat:OpenAI:TimeoutSeconds", "OPENAI_TIMEOUT_SECONDS", 60, 1, 300);

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

        _logger.LogInformation(
            "Calling OpenAI chat model. Endpoint={Endpoint}, Model={Model}, ForceJson={ForceJson}, TimeoutSeconds={TimeoutSeconds}, UserPromptLength={UserPromptLength}",
            endpoint,
            model,
            forceJson,
            timeoutSeconds,
            userPrompt.Length);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        var stopwatch = Stopwatch.StartNew();
        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, timeoutSeconds, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        _logger.LogInformation(
            "OpenAI response received. StatusCode={StatusCode}, ElapsedMs={ElapsedMs}, BodyLength={BodyLength}",
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            responseText.Length);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI returned non-success status. StatusCode={StatusCode}, Body={Body}", (int)response.StatusCode, TruncateForLog(responseText));
            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao chamar a OpenAI: {responseText}");
        }

        using var json = JsonDocument.Parse(responseText);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content) ? throw new InvalidOperationException("A OpenAI retornou uma resposta vazia.") : content;
    }

    private async Task<string> CallOllamaChatAsync(string systemPrompt, string userPrompt, bool forceJson, CancellationToken cancellationToken)
    {
        var model = GetString("Chat:Ollama:Model", "OLLAMA_MODEL", DefaultOllamaModel);
        var endpoint = BuildOllamaEndpoint(GetString("Chat:Ollama:BaseUrl", "OLLAMA_BASE_URL", DefaultOllamaBaseUrl));
        var timeoutSeconds = GetInt("Chat:Ollama:TimeoutSeconds", "OLLAMA_TIMEOUT_SECONDS", 120, 1, 600);
        var useJsonFormat = GetBool("Chat:Ollama:UseJsonFormat", "OLLAMA_USE_JSON_FORMAT", true);

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

        _logger.LogInformation(
            "Calling Ollama chat model. Endpoint={Endpoint}, Model={Model}, ForceJson={ForceJson}, UseJsonFormat={UseJsonFormat}, TimeoutSeconds={TimeoutSeconds}, UserPromptLength={UserPromptLength}",
            endpoint,
            model,
            forceJson,
            useJsonFormat,
            timeoutSeconds,
            userPrompt.Length);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        var stopwatch = Stopwatch.StartNew();
        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, timeoutSeconds, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        _logger.LogInformation(
            "Ollama response received. StatusCode={StatusCode}, ElapsedMs={ElapsedMs}, BodyLength={BodyLength}, Body={Body}",
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            responseText.Length,
            GetOptionalLogText("Logging:Chat:IncludeModelResponses", "LOG_CHAT_INCLUDE_MODEL_RESPONSES", responseText));

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Ollama returned non-success status. StatusCode={StatusCode}, Body={Body}", (int)response.StatusCode, TruncateForLog(responseText));
            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao chamar o Ollama: {responseText}");
        }

        using var json = JsonDocument.Parse(responseText);
        var root = json.RootElement;
        string? content = null;

        if (root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var messageContent))
        {
            content = messageContent.GetString();
        }
        else if (root.TryGetProperty("response", out var responseElement))
        {
            content = responseElement.GetString();
        }

        return string.IsNullOrWhiteSpace(content) ? throw new InvalidOperationException("O Ollama retornou uma resposta vazia.") : content;
    }

    private string GetProvider()
    {
        return GetString("Chat:Provider", "CHAT_PROVIDER", DefaultProvider).Trim().ToLowerInvariant();
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
        var rawValue = GetString(configurationKey, environmentKey, defaultValue.ToString());
        return int.TryParse(rawValue, out var value) ? Math.Clamp(value, min, max) : defaultValue;
    }

    private bool GetBool(string configurationKey, string environmentKey, bool defaultValue)
    {
        var rawValue = GetString(configurationKey, environmentKey, defaultValue.ToString());
        return bool.TryParse(rawValue, out var value) ? value : defaultValue;
    }

    private string? GetOptionalLogText(string configurationKey, string environmentKey, string value)
    {
        return GetBool(configurationKey, environmentKey, true) ? TruncateForLog(value) : null;
    }

    private string TruncateForLog(string value)
    {
        var maxChars = GetInt("Logging:Chat:MaxLoggedBodyChars", "LOG_CHAT_MAX_BODY_CHARS", 10000, 100, 500000);
        return value.Length <= maxChars ? value : value[..maxChars] + "\n... log truncado ...";
    }

    private static async Task<HttpResponseMessage> SendAsyncWithTimeout(HttpClient httpClient, HttpRequestMessage request, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return await httpClient.SendAsync(request, timeoutCts.Token);
    }

    private static Uri BuildOpenAiEndpoint(string baseUrl) => new(new Uri(EnsureTrailingSlash(baseUrl)), "chat/completions");

    private static Uri BuildOllamaEndpoint(string baseUrl) => new(new Uri(EnsureTrailingSlash(baseUrl)), "api/chat");

    private static Uri BuildUri(string baseUrl, string relativePath, IReadOnlyDictionary<string, string?> query)
    {
        var builder = new UriBuilder(new Uri(new Uri(EnsureTrailingSlash(baseUrl)), relativePath));
        builder.Query = string.Join("&", query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
        return builder.Uri;
    }

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private static string? NormalizeToolName(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool))
        {
            return tool;
        }

        var value = tool.Trim().ToLowerInvariant().Replace('-', '_');
        return value switch
        {
            "search_depetados" or "search_deputado" or "deputados_search" or "buscar_deputados" or "listar_deputados" or "deputados" => "search_deputados",
            "get_deputados" or "deputado" or "detalhe_deputado" => "get_deputado",
            "search_proposicao" or "buscar_proposicoes" or "listar_proposicoes" or "proposicoes" => "search_proposicoes",
            "get_proposicoes" or "proposicao" or "detalhe_proposicao" => "get_proposicao",
            "search_evento" or "buscar_eventos" or "eventos" => "search_eventos",
            "buscar_orgaos" or "orgaos" or "comissoes" => "search_orgaos",
            _ => value
        };
    }

    private static Dictionary<string, string?> NormalizeArguments(string tool, IReadOnlyDictionary<string, string?> arguments)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            var name = NormalizeArgumentName(tool, argument.Key);
            if (name is null || string.IsNullOrWhiteSpace(argument.Value))
            {
                continue;
            }

            result[name] = NormalizeArgumentValue(name, argument.Value);
        }

        return result;
    }

    private static string? NormalizeArgumentName(string tool, string name)
    {
        var key = name.Trim().ToLowerInvariant().Replace("-", string.Empty).Replace("_", string.Empty);
        return tool.ToLowerInvariant() switch
        {
            "search_deputados" => key switch
            {
                "nome" or "name" or "nomedeputado" or "deputado" or "deputada" => "nome",
                "siglauf" or "uf" or "estado" or "siglaestado" => "siglaUf",
                "siglapartido" or "partido" or "party" => "siglaPartido",
                "idlegislatura" or "legislatura" => "idLegislatura",
                "pagina" or "page" => "pagina",
                "itens" or "items" or "limit" or "limite" or "quantidade" => "itens",
                _ => null
            },
            "get_deputado" => key switch
            {
                "iddeputado" or "id" or "deputadoid" => "idDeputado",
                _ => null
            },
            "search_proposicoes" => key switch
            {
                "siglatipo" or "tipo" => "siglaTipo",
                "numero" or "number" => "numero",
                "ano" or "year" => "ano",
                "ementa" or "assunto" or "tema" or "texto" or "query" or "q" => "ementa",
                "autor" or "author" => "autor",
                "datainicio" or "inicio" or "startdate" => "dataInicio",
                "datafim" or "fim" or "enddate" => "dataFim",
                "pagina" or "page" => "pagina",
                "itens" or "items" or "limit" or "limite" or "quantidade" => "itens",
                _ => null
            },
            "get_proposicao" => key switch
            {
                "idproposicao" or "id" or "proposicaoid" => "idProposicao",
                _ => null
            },
            "search_eventos" => key switch
            {
                "datainicio" or "inicio" or "startdate" => "dataInicio",
                "datafim" or "fim" or "enddate" => "dataFim",
                "descricao" or "description" or "assunto" or "tema" or "query" or "q" => "descricao",
                "pagina" or "page" => "pagina",
                "itens" or "items" or "limit" or "limite" or "quantidade" => "itens",
                _ => null
            },
            "search_orgaos" => key switch
            {
                "sigla" => "sigla",
                "nome" or "name" or "orgao" or "comissao" => "nome",
                "pagina" or "page" => "pagina",
                "itens" or "items" or "limit" or "limite" or "quantidade" => "itens",
                _ => null
            },
            _ => null
        };
    }

    private static string NormalizeArgumentValue(string name, string value)
    {
        var cleanValue = value.Trim();
        return name is "siglaUf" or "siglaPartido" or "siglaTipo" or "sigla" ? cleanValue.ToUpperInvariant() : cleanValue;
    }

    private static string Required(IReadOnlyDictionary<string, string?> args, string key)
    {
        return args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"O argumento obrigatório '{key}' não foi informado.");
    }

    private static bool IsIdentifierArgument(string key)
    {
        return key.Equals("idDeputado", StringComparison.OrdinalIgnoreCase)
            || key.Equals("idProposicao", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> EmptyQuery() => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

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
