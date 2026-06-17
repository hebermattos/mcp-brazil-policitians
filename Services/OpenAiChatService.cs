using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class OpenAiChatService
{
    private const string DefaultOpenAiModel = "gpt-4.1-mini";
    private const string DefaultCamaraBaseUrl = "https://dadosabertos.camara.leg.br/api/v2/";

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
        var systemPrompt = """
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
- Use itens no máximo 10.
- Quando o usuário informar UF, use siglaUf com duas letras maiúsculas.
- Quando o usuário informar ano, use ano numérico.
- Quando buscar por assunto de proposição, coloque o assunto principal em ementa.

Formato obrigatório:
{
  "tool": "search_proposicoes",
  "arguments": {
    "ementa": "escala 6x1",
    "itens": 10,
    "pagina": 1
  }
}
""";

        var content = await CallOpenAiChatAsync(
            systemPrompt,
            prompt,
            forceJson: true,
            cancellationToken);

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

            if (!arguments.ContainsKey("itens") && tool.StartsWith("search_", StringComparison.OrdinalIgnoreCase))
            {
                arguments["itens"] = "10";
            }

            if (!arguments.ContainsKey("pagina") && tool.StartsWith("search_", StringComparison.OrdinalIgnoreCase))
            {
                arguments["pagina"] = "1";
            }

            return new ToolPlan(tool, arguments);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "OpenAI returned an invalid tool plan: {Content}", content);
            throw new InvalidOperationException("Não foi possível interpretar o plano de ferramenta retornado pela OpenAI.", ex);
        }
    }

    private async Task<JsonDocument> ExecuteCamaraToolAsync(ToolPlan plan, CancellationToken cancellationToken)
    {
        var (path, query) = BuildCamaraRequest(plan);
        var baseUrl = GetConfigurationValue("CAMARA_API_BASE_URL") ?? DefaultCamaraBaseUrl;
        var requestUri = BuildUri(baseUrl, path, query);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request, cancellationToken);
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
        var dataJson = data.RootElement.GetRawText();
        if (dataJson.Length > 20_000)
        {
            dataJson = dataJson[..20_000] + "\n... conteúdo truncado ...";
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

        return await CallOpenAiChatAsync(systemPrompt, userContent, forceJson: false, cancellationToken);
    }

    private async Task<string> CallOpenAiChatAsync(
        string systemPrompt,
        string userPrompt,
        bool forceJson,
        CancellationToken cancellationToken)
    {
        var apiKey = GetConfigurationValue("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Configure a variável de ambiente OPENAI_API_KEY antes de usar o chat com LLM.");
        }

        var model = GetConfigurationValue("OPENAI_MODEL") ?? DefaultOpenAiModel;

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

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        var httpClient = _httpClientFactory.CreateClient();
        using var response = await httpClient.SendAsync(request, cancellationToken);
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

    private string? GetConfigurationValue(string key)
    {
        return _configuration[key] ?? Environment.GetEnvironmentVariable(key);
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
