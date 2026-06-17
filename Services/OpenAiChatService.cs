using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class OpenAiChatService
{
    private const string DefaultProvider = "ollama";
    private const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1/";
    private const string DefaultOpenAiModel = "gpt-4.1-mini";
    private const string DefaultOllamaBaseUrl = "http://localhost:11434";
    private const string DefaultOllamaModel = "llama3.2:1B";
    private const string DefaultCamaraBaseUrl = "https://dadosabertos.camara.leg.br/api/v2/";

    private static readonly HashSet<string> KnownPropositionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PL", "PLP", "PEC", "MPV", "PDL", "PRC", "PLV", "MSC", "REQ", "RIC", "INC", "RCP", "PDC", "SBT", "EMC"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenAiChatService> _logger;
    private readonly PromptFileLogService _promptFileLogService;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public OpenAiChatService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<OpenAiChatService> logger,
        PromptFileLogService promptFileLogService)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _promptFileLogService = promptFileLogService;
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
        var promptLog = _promptFileLogService.Start(operationId, prompt, provider);

        _logger.LogInformation(
            "Chat request started. Provider={Provider}, PromptLength={PromptLength}, Prompt={Prompt}, PromptLogFile={PromptLogFile}",
            provider,
            prompt.Length,
            GetOptionalLogText("Logging:Chat:IncludePromptContent", "LOG_CHAT_INCLUDE_PROMPT_CONTENT", prompt),
            promptLog?.FilePath);

        try
        {
            var plan = await CreateToolPlanAsync(prompt, promptLog, cancellationToken);
            using var data = await ExecuteCamaraToolAsync(plan, promptLog, cancellationToken);
            var answer = await CreateFinalAnswerAsync(prompt, plan, data, promptLog, cancellationToken);

            stopwatch.Stop();
            var completionData = new
            {
                provider,
                plan.Tool,
                plan.Arguments,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                answerLength = answer.Length
            };

            _logger.LogInformation(
                "Chat request completed. Provider={Provider}, Tool={Tool}, Arguments={Arguments}, ElapsedMs={ElapsedMs}, PromptLogFile={PromptLogFile}",
                provider,
                plan.Tool,
                JsonSerializer.Serialize(plan.Arguments, _jsonOptions),
                stopwatch.ElapsedMilliseconds,
                promptLog?.FilePath);

            _promptFileLogService.Complete(promptLog, completionData);

            return new ChatPromptResponse(
                answer,
                plan.Tool,
                plan.Arguments.ToDictionary(x => x.Key, x => (object?)x.Value, StringComparer.OrdinalIgnoreCase),
                data.RootElement.Clone(),
                promptLog?.FilePath);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Chat request failed. Provider={Provider}, ElapsedMs={ElapsedMs}, PromptLogFile={PromptLogFile}", provider, stopwatch.ElapsedMilliseconds, promptLog?.FilePath);
            _promptFileLogService.Fail(promptLog, ex, new { provider, elapsedMs = stopwatch.ElapsedMilliseconds });
            throw;
        }
    }

    private async Task<ToolPlan> CreateToolPlanAsync(string prompt, PromptFileLogContext? promptLog, CancellationToken cancellationToken)
    {
        var defaultItems = GetInt("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", 10, 1, 100);
        var defaultPage = GetInt("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", 1, 1, 10000);

        if (TryCreateDeterministicToolPlan(prompt, defaultItems, defaultPage, out var deterministicPlan))
        {
            _logger.LogInformation(
                "Using deterministic tool plan. Tool={Tool}, Arguments={Arguments}",
                deterministicPlan.Tool,
                JsonSerializer.Serialize(deterministicPlan.Arguments, _jsonOptions));

            _promptFileLogService.Append(promptLog, "tool-plan.deterministic", new
            {
                deterministicPlan.Tool,
                deterministicPlan.Arguments
            });

            return deterministicPlan;
        }

        var systemPrompt = $$"""
Você é um planejador de ferramentas para a API de Dados Abertos da Câmara.
Responda apenas JSON válido, sem markdown.

Ferramentas permitidas e argumentos permitidos:
- search_deputados: nome, siglaUf, siglaPartido, idLegislatura, pagina, itens.
- get_deputado: idDeputado.
- search_proposicoes: siglaTipo, numero, ano, keywords, autor, dataInicio, dataFim, pagina, itens.
- get_proposicao: idProposicao.
- search_eventos: dataInicio, dataFim, descricao, pagina, itens.
- search_orgaos: sigla, nome, pagina, itens.

Regras:
- Para deputados, parlamentares, partido ou deputados por partido, use search_deputados.
- Para projetos de lei, PEC, MP, proposições ou temas legislativos, use search_proposicoes.
- Para busca por assunto de proposição, use keywords. Nunca use ementa.
- Use pagina {{defaultPage}} e itens no máximo {{defaultItems}} quando o usuário não informar.
- Para UF use siglaUf. Para partido use siglaPartido.
- Não invente autor, dataInicio, dataFim, siglaTipo, numero ou ano quando o usuário não informar explicitamente.
- Nunca crie nomes de ferramentas ou argumentos fora da lista permitida.

Formato:
{
  "tool": "search_proposicoes",
  "arguments": {
    "keywords": "escala 6x1",
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

        _promptFileLogService.Append(promptLog, "tool-plan.request", new
        {
            provider = GetProvider(),
            defaultPage,
            defaultItems,
            forceJson = true,
            systemPrompt = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelPrompts", "LOG_PROMPT_FILE_INCLUDE_MODEL_PROMPTS", systemPrompt),
            userPrompt = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludePromptContent", "LOG_PROMPT_FILE_INCLUDE_PROMPT_CONTENT", prompt)
        });

        var content = await CallChatModelAsync(systemPrompt, prompt, forceJson: true, purpose: "tool-plan", promptLog, cancellationToken);

        _logger.LogInformation(
            "Raw tool plan returned by model. Content={Content}",
            GetOptionalLogText("Logging:Chat:IncludeModelResponses", "LOG_CHAT_INCLUDE_MODEL_RESPONSES", content));

        _promptFileLogService.Append(promptLog, "tool-plan.raw-response", new
        {
            content = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelResponses", "LOG_PROMPT_FILE_INCLUDE_MODEL_RESPONSES", content)
        });

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
            normalizedArgs = RemoveHallucinatedArguments(tool, normalizedArgs, prompt);

            _logger.LogInformation(
                "Tool plan normalized. RawTool={RawTool}, Tool={Tool}, RawArguments={RawArguments}, NormalizedArguments={NormalizedArguments}",
                rawTool,
                tool,
                JsonSerializer.Serialize(rawArgs, _jsonOptions),
                JsonSerializer.Serialize(normalizedArgs, _jsonOptions));

            _promptFileLogService.Append(promptLog, "tool-plan.normalized", new
            {
                rawTool,
                tool,
                rawArguments = rawArgs,
                normalizedArguments = normalizedArgs
            });

            return new ToolPlan(tool, normalizedArgs);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Invalid tool plan returned by model. Content={Content}", content);
            _promptFileLogService.Fail(promptLog, ex, new { stage = "tool-plan.parse", content = _promptFileLogService.Truncate(content) });
            throw new InvalidOperationException("Não foi possível interpretar o plano de ferramenta retornado pelo modelo.", ex);
        }
    }

    private async Task<JsonDocument> ExecuteCamaraToolAsync(ToolPlan plan, PromptFileLogContext? promptLog, CancellationToken cancellationToken)
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

        _promptFileLogService.Append(promptLog, "camara-api.request", new
        {
            plan.Tool,
            path,
            query,
            url = requestUri.ToString(),
            timeoutSeconds
        });

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

        _promptFileLogService.Append(promptLog, "camara-api.response", new
        {
            statusCode = (int)response.StatusCode,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            bodyLength = responseText.Length,
            body = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeCamaraResponseBody", "LOG_PROMPT_FILE_INCLUDE_CAMARA_RESPONSE_BODY", responseText)
        });

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

    private async Task<string> CreateFinalAnswerAsync(string prompt, ToolPlan plan, JsonDocument data, PromptFileLogContext? promptLog, CancellationToken cancellationToken)
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

        _promptFileLogService.Append(promptLog, "final-answer.request", new
        {
            plan.Tool,
            plan.Arguments,
            dataJsonLength = dataJson.Length,
            maxDataJsonChars = maxChars,
            systemPrompt = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelPrompts", "LOG_PROMPT_FILE_INCLUDE_MODEL_PROMPTS", systemPrompt),
            userPrompt = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelPrompts", "LOG_PROMPT_FILE_INCLUDE_MODEL_PROMPTS", userContent)
        });

        var answer = await CallChatModelAsync(systemPrompt, userContent, forceJson: false, purpose: "final-answer", promptLog, cancellationToken);

        _logger.LogInformation(
            "Final answer generated. AnswerLength={AnswerLength}, Answer={Answer}",
            answer.Length,
            GetOptionalLogText("Logging:Chat:IncludeModelResponses", "LOG_CHAT_INCLUDE_MODEL_RESPONSES", answer));

        _promptFileLogService.Append(promptLog, "final-answer.response", new
        {
            answerLength = answer.Length,
            answer = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelResponses", "LOG_PROMPT_FILE_INCLUDE_MODEL_RESPONSES", answer)
        });

        return answer;
    }

    private Task<string> CallChatModelAsync(
        string systemPrompt,
        string userPrompt,
        bool forceJson,
        string purpose,
        PromptFileLogContext? promptLog,
        CancellationToken cancellationToken)
    {
        return GetProvider() switch
        {
            "openai" => CallOpenAiChatAsync(systemPrompt, userPrompt, forceJson, purpose, promptLog, cancellationToken),
            "ollama" => CallOllamaChatAsync(systemPrompt, userPrompt, forceJson, purpose, promptLog, cancellationToken),
            var provider => throw new InvalidOperationException($"Provedor de chat inválido: '{provider}'. Use 'openai' ou 'ollama'.")
        };
    }

    private async Task<string> CallOpenAiChatAsync(
        string systemPrompt,
        string userPrompt,
        bool forceJson,
        string purpose,
        PromptFileLogContext? promptLog,
        CancellationToken cancellationToken)
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
            "Calling OpenAI chat model. Endpoint={Endpoint}, Model={Model}, ForceJson={ForceJson}, TimeoutSeconds={TimeoutSeconds}, UserPromptLength={UserPromptLength}, Purpose={Purpose}",
            endpoint,
            model,
            forceJson,
            timeoutSeconds,
            userPrompt.Length,
            purpose);

        _promptFileLogService.Append(promptLog, "openai.request", new
        {
            purpose,
            endpoint = endpoint.ToString(),
            model,
            forceJson,
            timeoutSeconds,
            userPromptLength = userPrompt.Length
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        var stopwatch = Stopwatch.StartNew();
        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, timeoutSeconds, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        _logger.LogInformation(
            "OpenAI response received. StatusCode={StatusCode}, ElapsedMs={ElapsedMs}, BodyLength={BodyLength}, Purpose={Purpose}",
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            responseText.Length,
            purpose);

        _promptFileLogService.Append(promptLog, "openai.response", new
        {
            purpose,
            statusCode = (int)response.StatusCode,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            bodyLength = responseText.Length,
            body = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelResponses", "LOG_PROMPT_FILE_INCLUDE_MODEL_RESPONSES", responseText)
        });

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenAI returned non-success status. StatusCode={StatusCode}, Body={Body}", (int)response.StatusCode, TruncateForLog(responseText));
            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} ao chamar a OpenAI: {responseText}");
        }

        using var json = JsonDocument.Parse(responseText);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content) ? throw new InvalidOperationException("A OpenAI retornou uma resposta vazia.") : content;
    }

    private async Task<string> CallOllamaChatAsync(
        string systemPrompt,
        string userPrompt,
        bool forceJson,
        string purpose,
        PromptFileLogContext? promptLog,
        CancellationToken cancellationToken)
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
            "Calling Ollama chat model. Endpoint={Endpoint}, Model={Model}, ForceJson={ForceJson}, UseJsonFormat={UseJsonFormat}, TimeoutSeconds={TimeoutSeconds}, UserPromptLength={UserPromptLength}, Purpose={Purpose}",
            endpoint,
            model,
            forceJson,
            useJsonFormat,
            timeoutSeconds,
            userPrompt.Length,
            purpose);

        _promptFileLogService.Append(promptLog, "ollama.request", new
        {
            purpose,
            endpoint = endpoint.ToString(),
            model,
            forceJson,
            useJsonFormat,
            timeoutSeconds,
            userPromptLength = userPrompt.Length
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        var stopwatch = Stopwatch.StartNew();
        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, timeoutSeconds, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        _logger.LogInformation(
            "Ollama response received. StatusCode={StatusCode}, ElapsedMs={ElapsedMs}, BodyLength={BodyLength}, Body={Body}, Purpose={Purpose}",
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            responseText.Length,
            GetOptionalLogText("Logging:Chat:IncludeModelResponses", "LOG_CHAT_INCLUDE_MODEL_RESPONSES", responseText),
            purpose);

        _promptFileLogService.Append(promptLog, "ollama.response", new
        {
            purpose,
            statusCode = (int)response.StatusCode,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            bodyLength = responseText.Length,
            body = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelResponses", "LOG_PROMPT_FILE_INCLUDE_MODEL_RESPONSES", responseText)
        });

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

    private static bool TryCreateDeterministicToolPlan(string prompt, int defaultItems, int defaultPage, out ToolPlan plan)
    {
        var normalizedPrompt = NormalizePromptText(prompt);
        var args = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["pagina"] = defaultPage.ToString(CultureInfo.InvariantCulture),
            ["itens"] = defaultItems.ToString(CultureInfo.InvariantCulture)
        };

        if (LooksLikeDeputadosSearch(normalizedPrompt))
        {
            var uf = ExtractBrazilianStateUf(prompt);
            if (!string.IsNullOrWhiteSpace(uf))
            {
                args["siglaUf"] = uf;
            }

            var partido = ExtractParty(prompt);
            if (!string.IsNullOrWhiteSpace(partido))
            {
                args["siglaPartido"] = partido;
            }

            plan = new ToolPlan("search_deputados", NormalizeArguments("search_deputados", args));
            return true;
        }

        if (LooksLikeProposicoesSearch(normalizedPrompt))
        {
            var subject = ExtractSubjectKeywords(prompt);
            if (!string.IsNullOrWhiteSpace(subject))
            {
                args["keywords"] = subject;
            }

            var typeAndNumber = ExtractPropositionTypeAndNumber(prompt);
            if (typeAndNumber is not null)
            {
                args["siglaTipo"] = typeAndNumber.Value.Type;
                args["numero"] = typeAndNumber.Value.Number;
            }

            var year = ExtractYear(prompt);
            if (!string.IsNullOrWhiteSpace(year))
            {
                args["ano"] = year;
            }

            plan = new ToolPlan("search_proposicoes", NormalizeArguments("search_proposicoes", args));
            return true;
        }

        plan = new ToolPlan(string.Empty, EmptyQuery());
        return false;
    }

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

            var value = NormalizeArgumentValue(name, argument.Value);
            if (!IsValidArgumentValue(name, value))
            {
                continue;
            }

            result[name] = value;
        }

        return result;
    }

    private static Dictionary<string, string?> RemoveHallucinatedArguments(string tool, Dictionary<string, string?> arguments, string prompt)
    {
        if (!tool.Equals("search_proposicoes", StringComparison.OrdinalIgnoreCase))
        {
            return arguments;
        }

        var promptText = NormalizePromptText(prompt);
        var result = new Dictionary<string, string?>(arguments, StringComparer.OrdinalIgnoreCase);

        RemoveIfValueNotInPrompt(result, "autor", promptText);
        RemoveIfValueNotInPrompt(result, "siglaTipo", promptText);
        RemoveIfValueNotInPrompt(result, "numero", promptText);
        RemoveIfValueNotInPrompt(result, "ano", promptText);

        if (result.TryGetValue("dataInicio", out var dataInicio) && !PromptMentionsYearOrDate(promptText, dataInicio))
        {
            result.Remove("dataInicio");
        }

        if (result.TryGetValue("dataFim", out var dataFim) && !PromptMentionsYearOrDate(promptText, dataFim))
        {
            result.Remove("dataFim");
        }

        if (!result.ContainsKey("keywords"))
        {
            var subject = ExtractSubjectKeywords(prompt);
            if (!string.IsNullOrWhiteSpace(subject))
            {
                result["keywords"] = subject;
            }
        }

        return result;
    }

    private static void RemoveIfValueNotInPrompt(Dictionary<string, string?> arguments, string key, string normalizedPrompt)
    {
        if (!arguments.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalizedValue = NormalizePromptText(value);
        if (!normalizedPrompt.Contains(normalizedValue, StringComparison.OrdinalIgnoreCase))
        {
            arguments.Remove(key);
        }
    }

    private static bool PromptMentionsYearOrDate(string normalizedPrompt, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var yearMatch = Regex.Match(value, @"\b(19|20)\d{2}\b");
        return yearMatch.Success && normalizedPrompt.Contains(yearMatch.Value, StringComparison.OrdinalIgnoreCase);
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
                "ano" or "anho" or "year" => "ano",
                "keywords" or "keyword" or "palavraschave" or "palavrachave" or "ementa" or "assunto" or "tema" or "texto" or "query" or "q" => "keywords",
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
        return name switch
        {
            "siglaUf" or "siglaPartido" or "siglaTipo" or "sigla" => cleanValue.ToUpperInvariant(),
            "dataInicio" or "dataFim" => NormalizeDate(cleanValue),
            _ => cleanValue
        };
    }

    private static bool IsValidArgumentValue(string name, string value)
    {
        return name switch
        {
            "siglaTipo" => KnownPropositionTypes.Contains(value),
            "numero" => Regex.IsMatch(value, "^[0-9]+$"),
            "ano" => Regex.IsMatch(value, "^(19|20)[0-9]{2}$"),
            "dataInicio" or "dataFim" => Regex.IsMatch(value, "^[0-9]{4}-[0-9]{2}-[0-9]{2}$"),
            "pagina" or "itens" or "idDeputado" or "idProposicao" or "idLegislatura" => Regex.IsMatch(value, "^[0-9]+$"),
            _ => true
        };
    }

    private static string NormalizeDate(string value)
    {
        var formats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy" };
        return DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value;
    }

    private static bool LooksLikeDeputadosSearch(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("deputado", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("deputada", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("parlamentar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeProposicoesSearch(string normalizedPrompt)
    {
        return normalizedPrompt.Contains("proposicao", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("proposicoes", StringComparison.OrdinalIgnoreCase)
            || normalizedPrompt.Contains("projeto", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(normalizedPrompt, @"\b(pl|plp|pec|mpv|pdl|prc|req)\b", RegexOptions.IgnoreCase);
    }

    private static string? ExtractSubjectKeywords(string prompt)
    {
        var normalized = Regex.Replace(prompt.Trim(), "\\s+", " ");
        var match = Regex.Match(normalized, @"\b(?:sobre|acerca de|relacionad[ao]s? a|com tema)\b\s+(?<subject>.+)$", RegexOptions.IgnoreCase);
        var subject = match.Success ? match.Groups["subject"].Value : normalized;

        subject = Regex.Replace(subject, @"\b(?:retorne|retornar|liste|listar|mostre|mostrar|busque|buscar|procure|procurar)\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        subject = Regex.Replace(subject, @"^(?:a|o|as|os|de|da|do|das|dos)\s+", string.Empty, RegexOptions.IgnoreCase).Trim();

        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }

    private static string? ExtractBrazilianStateUf(string prompt)
    {
        var match = Regex.Match(prompt, @"\b(AC|AL|AP|AM|BA|CE|DF|ES|GO|MA|MT|MS|MG|PA|PB|PR|PE|PI|RJ|RN|RS|RO|RR|SC|SP|SE|TO)\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    private static string? ExtractParty(string prompt)
    {
        var match = Regex.Match(prompt, @"\b(PT|PL|MDB|PSD|PP|PSB|PSOL|NOVO|REPUBLICANOS|UNIÃO|UNIAO|PDT|PCDOB|PV|PSDB|CIDADANIA|AVANTE|SOLIDARIEDADE|PODE|PODEMOS|PRD)\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant().Replace("UNIÃO", "UNIAO") : null;
    }

    private static (string Type, string Number)? ExtractPropositionTypeAndNumber(string prompt)
    {
        var match = Regex.Match(prompt, @"\b(?<type>PLP|PEC|MPV|PDL|PRC|REQ|PL)\s*(?<number>[0-9]+)\b", RegexOptions.IgnoreCase);
        return match.Success
            ? (match.Groups["type"].Value.ToUpperInvariant(), match.Groups["number"].Value)
            : null;
    }

    private static string? ExtractYear(string prompt)
    {
        var match = Regex.Match(prompt, @"\b(19|20)[0-9]{2}\b");
        return match.Success ? match.Value : null;
    }

    private static string NormalizePromptText(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        normalized = normalized
            .Replace("á", "a").Replace("à", "a").Replace("ã", "a").Replace("â", "a")
            .Replace("é", "e").Replace("ê", "e")
            .Replace("í", "i")
            .Replace("ó", "o").Replace("õ", "o").Replace("ô", "o")
            .Replace("ú", "u")
            .Replace("ç", "c");

        return Regex.Replace(normalized, "\\s+", " ");
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
