using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class GenericChatPlanQueryService
{
    private const string DefaultProvider = "ollama";
    private const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1/";
    private const string DefaultOpenAiModel = "gpt-4.1-mini";
    private const string DefaultOllamaBaseUrl = "http://localhost:11434";
    private const string DefaultOllamaModel = "llama3.2:1B";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ChatPlanExecutorService _planExecutorService;
    private readonly ILogger<GenericChatPlanQueryService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public GenericChatPlanQueryService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ChatPlanExecutorService planExecutorService,
        ILogger<GenericChatPlanQueryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _planExecutorService = planExecutorService;
        _logger = logger;
    }

    public async Task<ChatPromptResponse?> TryHandleAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt) || !GetBool("Chat:GenericPlanner:Enabled", "CHAT_GENERIC_PLANNER_ENABLED", true))
        {
            return null;
        }

        var plan = await CreatePlanAsync(prompt, cancellationToken);
        if (plan is null)
        {
            return null;
        }

        try
        {
            var executed = await _planExecutorService.ExecuteAsync(plan, cancellationToken);
            return new ChatPromptResponse(
                "Consulta executada por plano genérico multi-step.",
                executed.FinalTool,
                executed.FinalArguments,
                executed.FinalData,
                null);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Generic plan execution failed. Falling back. Plan={Plan}", JsonSerializer.Serialize(plan, _jsonOptions));
            return null;
        }
    }

    private async Task<ChatExecutionPlan?> CreatePlanAsync(string prompt, CancellationToken cancellationToken)
    {
        var defaultPage = GetInt("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", 1, 1, 10000);
        var defaultItems = GetInt("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", 10, 1, 100);
        var maxSteps = GetInt("Chat:GenericPlanner:MaxSteps", "CHAT_GENERIC_PLANNER_MAX_STEPS", 5, 1, 10);

        var systemPrompt = """
Você é um planejador de chamadas para a API da Câmara. Retorne somente JSON válido.
Formato: {"steps":[{"tool":"nome","arguments":{},"saveAs":"resultado"}],"finalResult":"resultado"}.
Ferramentas: search_deputados, get_deputado, get_deputado_despesas, get_deputado_discursos, get_deputado_eventos, get_deputado_frentes, get_deputado_orgaos, get_deputado_profissoes, get_deputado_votacoes, search_proposicoes, get_proposicao, get_proposicao_autores, get_proposicao_relacionadas, get_proposicao_temas, get_proposicao_tramitacoes, get_proposicao_votacoes, search_votacoes, get_votacao, get_votacao_orientacoes, get_votacao_votos, search_eventos, get_evento, search_orgaos, get_orgao, search_partidos, get_partido, search_frentes, search_grupos, search_legislaturas, search_blocos, search_referencias, camara_api_get.
Regras: não invente IDs; se precisar de ID, crie etapa anterior; use referências como {{proposicao.dados[0].id}}; para última/mais recente use ordem DESC; página padrão __DEFAULT_PAGE__; itens padrão __DEFAULT_ITEMS__; máximo __MAX_STEPS__ etapas.
Padrões importantes: votos de deputados de uma proposição => search_proposicoes -> get_proposicao_votacoes -> get_votacao_votos. Despesas por nome de deputado => search_deputados -> get_deputado_despesas. Proposições por assunto => search_proposicoes com keywords. Últimas votações => search_votacoes com ordem DESC e ordenarPor dataHoraRegistro.
"""
            .Replace("__DEFAULT_PAGE__", defaultPage.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__DEFAULT_ITEMS__", defaultItems.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__MAX_STEPS__", maxSteps.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        try
        {
            var content = await CallChatModelAsync(systemPrompt, prompt, cancellationToken);
            return ParsePlan(content, defaultPage, defaultItems, maxSteps);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not create generic plan from model response.");
            return null;
        }
    }

    private ChatExecutionPlan ParsePlan(string content, int defaultPage, int defaultItems, int maxSteps)
    {
        using var json = JsonDocument.Parse(StripMarkdownJsonFence(content));
        var root = json.RootElement;
        var stepsElement = root.GetProperty("steps");
        if (stepsElement.ValueKind != JsonValueKind.Array || stepsElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Plano sem etapas.");
        }

        var steps = new List<ChatExecutionStep>();
        foreach (var step in stepsElement.EnumerateArray().Take(maxSteps))
        {
            var tool = step.GetProperty("tool").GetString();
            if (string.IsNullOrWhiteSpace(tool)) throw new InvalidOperationException("Etapa sem tool.");

            var arguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (step.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in args.EnumerateObject())
                {
                    var value = ToStringValue(property.Value);
                    if (!string.IsNullOrWhiteSpace(value)) arguments[property.Name] = value;
                }
            }

            if (tool.StartsWith("search_", StringComparison.OrdinalIgnoreCase))
            {
                arguments.TryAdd("pagina", defaultPage.ToString(CultureInfo.InvariantCulture));
                arguments.TryAdd("itens", defaultItems.ToString(CultureInfo.InvariantCulture));
            }

            var saveAs = step.TryGetProperty("saveAs", out var saveAsElement) ? saveAsElement.GetString() : null;
            steps.Add(new ChatExecutionStep(tool, arguments, string.IsNullOrWhiteSpace(saveAs) ? $"resultado{steps.Count + 1}" : saveAs));
        }

        var finalResult = root.TryGetProperty("finalResult", out var final) ? final.GetString() : steps.Last().SaveAs;
        return new ChatExecutionPlan(steps, finalResult);
    }

    private async Task<string> CallChatModelAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        return GetProvider() == "openai"
            ? await CallOpenAiAsync(systemPrompt, userPrompt, cancellationToken)
            : await CallOllamaAsync(systemPrompt, userPrompt, cancellationToken);
    }

    private async Task<string> CallOpenAiAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var apiKey = GetString("Chat:OpenAI:ApiKey", "OPENAI_API_KEY", string.Empty);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("OPENAI_API_KEY não configurada.");

        var endpoint = new Uri(new Uri(EnsureTrailingSlash(GetString("Chat:OpenAI:BaseUrl", "OPENAI_BASE_URL", DefaultOpenAiBaseUrl))), "chat/completions");
        var payload = new
        {
            model = GetString("Chat:OpenAI:Model", "OPENAI_MODEL", DefaultOpenAiModel),
            response_format = new { type = "json_object" },
            messages = new object[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private async Task<string> CallOllamaAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(new Uri(EnsureTrailingSlash(GetString("Chat:Ollama:BaseUrl", "OLLAMA_BASE_URL", DefaultOllamaBaseUrl))), "api/chat");
        var payload = new Dictionary<string, object?>
        {
            ["model"] = GetString("Chat:Ollama:Model", "OLLAMA_MODEL", DefaultOllamaModel),
            ["stream"] = false,
            ["format"] = "json",
            ["messages"] = new object[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private string GetProvider() => GetString("Chat:Provider", "CHAT_PROVIDER", DefaultProvider).Trim().ToLowerInvariant();
    private string GetString(string configurationKey, string environmentKey, string defaultValue) => !string.IsNullOrWhiteSpace(_configuration[configurationKey]) ? _configuration[configurationKey]! : Environment.GetEnvironmentVariable(environmentKey) ?? defaultValue;
    private int GetInt(string configurationKey, string environmentKey, int defaultValue, int min, int max) => int.TryParse(GetString(configurationKey, environmentKey, defaultValue.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, min, max) : defaultValue;
    private bool GetBool(string configurationKey, string environmentKey, bool defaultValue) => bool.TryParse(GetString(configurationKey, environmentKey, defaultValue.ToString()), out var value) ? value : defaultValue;
    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";
    private static string StripMarkdownJsonFence(string value) => value.Trim().TrimStart('`').Replace("json\n", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().TrimEnd('`').Trim();
    private static string? ToStringValue(JsonElement value) => value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null };
}
