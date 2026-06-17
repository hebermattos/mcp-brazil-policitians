using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class ChainedOpenAiChatService
{
    private const string DefaultProvider = "ollama";
    private const string DefaultOpenAiBaseUrl = "https://api.openai.com/v1/";
    private const string DefaultOpenAiModel = "gpt-4.1-mini";
    private const string DefaultOllamaBaseUrl = "http://localhost:11434";
    private const string DefaultOllamaModel = "llama3.2:1B";

    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedArguments =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["camara_api_get"] = Args("path", "pagina", "itens", "ordem", "ordenarPor"),
            ["search_blocos"] = Args("idLegislatura", "pagina", "itens"),
            ["get_bloco"] = Args("idBloco"),
            ["search_deputados"] = Args("nome", "siglaUf", "siglaPartido", "idLegislatura", "pagina", "itens"),
            ["get_deputado"] = Args("idDeputado"),
            ["get_deputado_despesas"] = Args("idDeputado", "ano", "mes", "ordenarPor", "ordem", "pagina", "itens"),
            ["get_deputado_discursos"] = Args("idDeputado", "dataInicio", "dataFim", "pagina", "itens"),
            ["get_deputado_eventos"] = Args("idDeputado", "dataInicio", "dataFim", "pagina", "itens"),
            ["get_deputado_frentes"] = Args("idDeputado", "pagina", "itens"),
            ["get_deputado_orgaos"] = Args("idDeputado", "pagina", "itens"),
            ["get_deputado_profissoes"] = Args("idDeputado"),
            ["get_deputado_votacoes"] = Args("idDeputado", "pagina", "itens"),
            ["search_eventos"] = Args("dataInicio", "dataFim", "descricao", "idOrgao", "pagina", "itens"),
            ["get_evento"] = Args("idEvento"),
            ["get_evento_orgaos"] = Args("idEvento"),
            ["get_evento_requerimentos"] = Args("idEvento"),
            ["get_evento_votacoes"] = Args("idEvento"),
            ["search_frentes"] = Args("idLegislatura", "pagina", "itens"),
            ["get_frente"] = Args("idFrente"),
            ["get_frente_membros"] = Args("idFrente", "pagina", "itens"),
            ["search_grupos"] = Args("idLegislatura", "pagina", "itens"),
            ["get_grupo"] = Args("idGrupo"),
            ["get_grupo_membros"] = Args("idGrupo", "pagina", "itens"),
            ["search_legislaturas"] = Args("dataInicio", "dataFim", "pagina", "itens"),
            ["get_legislatura"] = Args("idLegislatura"),
            ["search_orgaos"] = Args("sigla", "nome", "tipoOrgao", "pagina", "itens"),
            ["get_orgao"] = Args("idOrgao"),
            ["get_orgao_eventos"] = Args("idOrgao", "dataInicio", "dataFim", "pagina", "itens"),
            ["get_orgao_membros"] = Args("idOrgao", "idLegislatura", "pagina", "itens"),
            ["get_orgao_votacoes"] = Args("idOrgao", "dataInicio", "dataFim", "pagina", "itens"),
            ["search_partidos"] = Args("sigla", "pagina", "itens"),
            ["get_partido"] = Args("idPartido"),
            ["get_partido_membros"] = Args("idPartido", "idLegislatura", "pagina", "itens"),
            ["search_proposicoes"] = Args("siglaTipo", "numero", "ano", "keywords", "autor", "dataInicio", "dataFim", "pagina", "itens"),
            ["get_proposicao"] = Args("idProposicao"),
            ["get_proposicao_autores"] = Args("idProposicao"),
            ["get_proposicao_relacionadas"] = Args("idProposicao"),
            ["get_proposicao_temas"] = Args("idProposicao"),
            ["get_proposicao_tramitacoes"] = Args("idProposicao"),
            ["get_proposicao_votacoes"] = Args("idProposicao"),
            ["search_referencias"] = Args("tipo"),
            ["search_votacoes"] = Args("dataInicio", "dataFim", "idOrgao", "siglaUf", "pagina", "itens"),
            ["get_votacao"] = Args("idVotacao"),
            ["get_votacao_orientacoes"] = Args("idVotacao"),
            ["get_votacao_votos"] = Args("idVotacao")
        };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChainedOpenAiChatService> _logger;
    private readonly PromptFileLogService _promptFileLogService;
    private readonly CamaraToolExecutionService _toolExecutionService;
    private readonly ChatPlanExecutorService _planExecutorService;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public ChainedOpenAiChatService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ChainedOpenAiChatService> logger,
        PromptFileLogService promptFileLogService,
        CamaraToolExecutionService toolExecutionService,
        ChatPlanExecutorService planExecutorService)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _promptFileLogService = promptFileLogService;
        _toolExecutionService = toolExecutionService;
        _planExecutorService = planExecutorService;
    }

    public async Task<ChatPromptResponse> GetAnswerAsync(string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var operationId = Guid.NewGuid().ToString("N");
        var provider = GetProvider();
        var promptLog = _promptFileLogService.Start(operationId, prompt, provider);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var plannedRequest = await CreatePlannedRequestAsync(prompt, promptLog, cancellationToken);

            ChatPromptResponse response;
            if (plannedRequest.ExecutionPlan is not null)
            {
                var executedPlan = await _planExecutorService.ExecuteAsync(plannedRequest.ExecutionPlan, cancellationToken);
                var finalArguments = executedPlan.FinalArguments;
                var answerData = JsonSerializer.Serialize(new
                {
                    finalData = executedPlan.FinalData,
                    stepResults = executedPlan.StepResults
                }, _jsonOptions);

                var answer = await CreateFinalAnswerAsync(
                    prompt,
                    executedPlan.FinalTool,
                    finalArguments,
                    answerData,
                    promptLog,
                    cancellationToken);

                response = new ChatPromptResponse(
                    answer,
                    executedPlan.FinalTool,
                    finalArguments,
                    executedPlan.FinalData,
                    promptLog?.FilePath);
            }
            else if (plannedRequest.ToolPlan is not null)
            {
                using var data = await _toolExecutionService.ExecuteAsync(
                    plannedRequest.ToolPlan.Tool,
                    plannedRequest.ToolPlan.Arguments,
                    cancellationToken);

                var finalData = data.RootElement.Clone();
                var finalArguments = plannedRequest.ToolPlan.Arguments.ToDictionary(
                    item => item.Key,
                    item => (object?)item.Value,
                    StringComparer.OrdinalIgnoreCase);

                var answer = await CreateFinalAnswerAsync(
                    prompt,
                    plannedRequest.ToolPlan.Tool,
                    finalArguments,
                    finalData.GetRawText(),
                    promptLog,
                    cancellationToken);

                response = new ChatPromptResponse(
                    answer,
                    plannedRequest.ToolPlan.Tool,
                    finalArguments,
                    finalData,
                    promptLog?.FilePath);
            }
            else
            {
                throw new InvalidOperationException("O modelo não retornou um plano de ferramenta válido.");
            }

            stopwatch.Stop();
            _promptFileLogService.Complete(promptLog, new
            {
                provider,
                response.Tool,
                response.Arguments,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                answerLength = response.Answer.Length
            });

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Chat request failed. Provider={Provider}, ElapsedMs={ElapsedMs}, PromptLogFile={PromptLogFile}", provider, stopwatch.ElapsedMilliseconds, promptLog?.FilePath);
            _promptFileLogService.Fail(promptLog, ex, new { provider, elapsedMs = stopwatch.ElapsedMilliseconds });
            throw;
        }
    }

    private async Task<PlannedRequest> CreatePlannedRequestAsync(string prompt, PromptFileLogContext? promptLog, CancellationToken cancellationToken)
    {
        var defaultItems = GetInt("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", 10, 1, 100);
        var defaultPage = GetInt("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", 1, 1, 10000);
        var systemPrompt = BuildToolPlanningPrompt(defaultPage, defaultItems);

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
            var plannedRequest = ParsePlannedRequest(content, defaultPage, defaultItems);
            _promptFileLogService.Append(promptLog, "tool-plan.normalized", plannedRequest);
            return plannedRequest;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Invalid tool plan returned by model. Content={Content}", content);
            throw new InvalidOperationException("Não foi possível interpretar o plano de ferramenta retornado pelo modelo.", ex);
        }
    }

    private PlannedRequest ParsePlannedRequest(string content, int defaultPage, int defaultItems)
    {
        var jsonText = ExtractJsonObject(content);
        using var json = JsonDocument.Parse(jsonText);
        var root = json.RootElement;

        if (root.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == JsonValueKind.Array)
        {
            var steps = new List<ChatExecutionStep>();
            foreach (var stepElement in stepsElement.EnumerateArray())
            {
                var tool = NormalizeToolName(RequiredString(stepElement, "tool"));
                var arguments = NormalizeArguments(tool, ReadArguments(stepElement), defaultPage, defaultItems);
                var saveAs = OptionalString(stepElement, "saveAs") ?? OptionalString(stepElement, "save_as");

                steps.Add(new ChatExecutionStep(tool, arguments, saveAs));
            }

            if (steps.Count == 0)
            {
                throw new InvalidOperationException("O plano encadeado não possui etapas.");
            }

            var finalResult = OptionalString(root, "finalResult") ?? OptionalString(root, "final_result");
            return new PlannedRequest(null, new ChatExecutionPlan(steps, finalResult));
        }

        var singleTool = NormalizeToolName(RequiredString(root, "tool"));
        var singleArguments = NormalizeArguments(singleTool, ReadArguments(root), defaultPage, defaultItems);
        return new PlannedRequest(new ToolPlan(singleTool, singleArguments), null);
    }

    private async Task<string> CreateFinalAnswerAsync(
        string prompt,
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        string dataJson,
        PromptFileLogContext? promptLog,
        CancellationToken cancellationToken)
    {
        var maxChars = GetInt("Chat:MaxDataJsonChars", "CHAT_MAX_DATA_JSON_CHARS", 20000, 1000, 500000);
        if (dataJson.Length > maxChars)
        {
            dataJson = dataJson[..maxChars] + "\n... conteúdo truncado ...";
        }

        var systemPrompt = """
Você responde em português do Brasil usando somente o JSON retornado pelas tools da Câmara dos Deputados.
Não invente dados. Não preencha lacunas com suposições.
Se houver lista, resuma de forma legível. Se o usuário pedir tabela, retorne tabela Markdown com colunas relevantes.
Se o usuário pedir agrupamento ou totalização, use apenas os itens retornados e informe quando houver limitação de paginação.
Se não houver resultado, diga que não encontrou resultados com os filtros usados.
Ao final, quando útil, informe a tool executada, os argumentos e eventuais limitações.
""";

        var userContent = $"""
Pergunta do usuário:
{prompt}

Tool final executada:
{tool}

Argumentos finais:
{JsonSerializer.Serialize(arguments, _jsonOptions)}

JSON disponível:
{dataJson}
""";

        _promptFileLogService.Append(promptLog, "final-answer.request", new
        {
            tool,
            arguments,
            dataJsonLength = dataJson.Length,
            maxDataJsonChars = maxChars,
            systemPrompt = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelPrompts", "LOG_PROMPT_FILE_INCLUDE_MODEL_PROMPTS", systemPrompt),
            userPrompt = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelPrompts", "LOG_PROMPT_FILE_INCLUDE_MODEL_PROMPTS", userContent)
        });

        var answer = await CallChatModelAsync(systemPrompt, userContent, forceJson: false, purpose: "final-answer", promptLog, cancellationToken);

        _promptFileLogService.Append(promptLog, "final-answer.response", new
        {
            answerLength = answer.Length,
            answer = _promptFileLogService.GetOptionalLogText("Logging:PromptFile:IncludeModelResponses", "LOG_PROMPT_FILE_INCLUDE_MODEL_RESPONSES", answer)
        });

        return answer;
    }

    private async Task<string> CallChatModelAsync(
        string systemPrompt,
        string userPrompt,
        bool forceJson,
        string purpose,
        PromptFileLogContext? promptLog,
        CancellationToken cancellationToken)
    {
        return GetProvider() switch
        {
            "openai" => await CallOpenAiChatAsync(systemPrompt, userPrompt, forceJson, purpose, promptLog, cancellationToken),
            "ollama" => await CallOllamaChatAsync(systemPrompt, userPrompt, forceJson, purpose, promptLog, cancellationToken),
            var provider => throw new InvalidOperationException($"Provider de chat não suportado: {provider}")
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
        var model = GetString("Chat:OpenAI:Model", "OPENAI_MODEL", DefaultOpenAiModel);
        var endpoint = BuildOpenAiEndpoint(GetString("Chat:OpenAI:BaseUrl", "OPENAI_BASE_URL", DefaultOpenAiBaseUrl));
        var apiKey = GetString("Chat:OpenAI:ApiKey", "OPENAI_API_KEY", string.Empty);
        var timeoutSeconds = GetInt("Chat:OpenAI:TimeoutSeconds", "OPENAI_TIMEOUT_SECONDS", 60, 1, 300);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["temperature"] = 0,
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
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        request.Content = new StringContent(JsonSerializer.Serialize(payload, _jsonOptions), Encoding.UTF8, "application/json");

        var stopwatch = Stopwatch.StartNew();
        var httpClient = _httpClientFactory.CreateClient();
        using var response = await SendAsyncWithTimeout(httpClient, request, timeoutSeconds, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

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

    private static string BuildToolPlanningPrompt(int defaultPage, int defaultItems)
    {
        return """
Você é um planejador de tools MCP para consultar os Dados Abertos da Câmara dos Deputados do Brasil.
As tools encapsulam a API RESTful v2 da Câmara e retornam JSON.
Base REST equivalente: https://dadosabertos.camara.leg.br/api/v2/

Responda apenas JSON válido, sem markdown, sem comentários e sem texto fora do JSON.

FORMATOS PERMITIDOS

Use formato simples quando uma única chamada resolver a pergunta:
{
  "tool": "nome_da_tool",
  "arguments": {
    "argumento": "valor"
  }
}

Use formato encadeado quando precisar resolver um ID antes de consultar outro recurso:
{
  "steps": [
    {
      "tool": "search_deputados",
      "arguments": {
        "nome": "Nome do deputado",
        "pagina": "1",
        "itens": "1"
      },
      "saveAs": "deputado"
    },
    {
      "tool": "get_deputado_despesas",
      "arguments": {
        "idDeputado": "{{deputado.dados[0].id}}",
        "ano": "2024",
        "pagina": "1",
        "itens": "100"
      },
      "saveAs": "despesas"
    }
  ],
  "finalResult": "despesas"
}

PRINCÍPIO CENTRAL: ENCADEAMENTO
- Os recursos da API se relacionam por identificadores oficiais.
- Quando o usuário informar apenas nome, sigla, tema, número, ano ou outro filtro parcial, primeiro resolva a entidade usando uma tool search_*.
- Depois use o ID retornado em uma tool get_* ou em um sub-recurso.
- Nunca invente IDs.
- Nunca chute idDeputado, idProposicao, idVotacao, idOrgao, idEvento, idPartido, idFrente, idGrupo, idBloco ou idLegislatura.
- Números e anos de proposições só devem ser usados como filtros quando forem informados pelo usuário ou retornados por uma chamada anterior.

TOOLS DISPONÍVEIS

Fallback:
- camara_api_get: path, pagina, itens, ordem, ordenarPor. Use apenas quando nenhuma tool específica atender.

Deputados:
- search_deputados: nome, siglaPartido, siglaUf, idLegislatura, pagina, itens.
- get_deputado: idDeputado.
- get_deputado_despesas: idDeputado, ano, mes, ordenarPor, ordem, pagina, itens.
- get_deputado_discursos: idDeputado, dataInicio, dataFim, pagina, itens.
- get_deputado_eventos: idDeputado, dataInicio, dataFim, pagina, itens.
- get_deputado_orgaos: idDeputado, pagina, itens.
- get_deputado_frentes: idDeputado, pagina, itens.
- get_deputado_profissoes: idDeputado.
- get_deputado_votacoes: idDeputado, pagina, itens.

Proposições:
- search_proposicoes: siglaTipo, numero, ano, keywords, autor, dataInicio, dataFim, pagina, itens.
- get_proposicao: idProposicao.
- get_proposicao_autores: idProposicao.
- get_proposicao_tramitacoes: idProposicao.
- get_proposicao_temas: idProposicao.
- get_proposicao_votacoes: idProposicao.
- get_proposicao_relacionadas: idProposicao.

Votações:
- search_votacoes: dataInicio, dataFim, idOrgao, siglaUf, pagina, itens.
- get_votacao: idVotacao.
- get_votacao_votos: idVotacao.
- get_votacao_orientacoes: idVotacao.

Eventos e órgãos:
- search_eventos: dataInicio, dataFim, descricao, idOrgao, pagina, itens.
- get_evento: idEvento.
- get_evento_orgaos: idEvento.
- get_evento_requerimentos: idEvento.
- get_evento_votacoes: idEvento.
- search_orgaos: sigla, nome, tipoOrgao, pagina, itens.
- get_orgao: idOrgao.
- get_orgao_eventos: idOrgao, dataInicio, dataFim, pagina, itens.
- get_orgao_membros: idOrgao, idLegislatura, pagina, itens.
- get_orgao_votacoes: idOrgao, dataInicio, dataFim, pagina, itens.

Outros recursos:
- search_partidos: sigla, pagina, itens.
- get_partido: idPartido.
- get_partido_membros: idPartido, idLegislatura, pagina, itens.
- search_frentes: idLegislatura, pagina, itens.
- get_frente: idFrente.
- get_frente_membros: idFrente, pagina, itens.
- search_grupos: idLegislatura, pagina, itens.
- get_grupo: idGrupo.
- get_grupo_membros: idGrupo, pagina, itens.
- search_blocos: idLegislatura, pagina, itens.
- get_bloco: idBloco.
- search_legislaturas: dataInicio, dataFim, pagina, itens.
- get_legislatura: idLegislatura.
- search_referencias: tipo.

REGRAS DE USO
- Use pagina {DEFAULT_PAGE} e itens no máximo {DEFAULT_ITEMS} quando o usuário não informar paginação.
- Use itens no máximo 100.
- Resolva nomes, siglas, temas ou filtros parciais antes de acessar sub-recursos por ID.
- Se uma busca puder retornar mais de um resultado, use pagina 1 e itens 5 ou 10 para apresentar opções; use itens 1 apenas quando a pergunta indicar uma entidade claramente específica.
- Se a pergunta pedir dados de um deputado pelo nome, use search_deputados antes de get_deputado_*.
- Se a pergunta pedir dados de uma proposição por tipo/número/ano/tema, use search_proposicoes antes de get_proposicao_*.
- Use ano e mes para despesas.
- Use dataInicio e dataFim para recortes temporais.
- Use ordenarPor e ordem para ordenação quando aplicável.
- Para UF use siglaUf somente quando houver sigla real de estado brasileiro ou nome de estado. Não interprete a palavra "se" como Sergipe.
- Para partido use siglaPartido em search_deputados; use search_partidos quando precisar de idPartido.
- Datas devem estar em yyyy-MM-dd.
- Nunca use argumentos fora da lista permitida da tool escolhida.

PADRÕES DE CADEIA FREQUENTES
- Gasto de um deputado: search_deputados -> get_deputado_despesas.
- Como partidos votaram em uma proposição: search_proposicoes -> get_proposicao_votacoes -> get_votacao_orientacoes.
- Quem de um partido votou contra em uma votação: get_votacao_votos + search_partidos -> get_partido_membros -> cruzar dados na resposta final.
- Autores de uma proposição por tema: search_proposicoes -> get_proposicao_autores.
- Tramitação de uma proposição: search_proposicoes -> get_proposicao_tramitacoes.
- Eventos de um órgão: search_orgaos -> get_orgao_eventos.

EXEMPLOS

Usuário: Quanto a deputada Erika Hilton gastou em 2024?
Resposta:
{
  "steps": [
    {
      "tool": "search_deputados",
      "arguments": {
        "nome": "Erika Hilton",
        "pagina": "1",
        "itens": "1"
      },
      "saveAs": "deputado"
    },
    {
      "tool": "get_deputado_despesas",
      "arguments": {
        "idDeputado": "{{deputado.dados[0].id}}",
        "ano": "2024",
        "pagina": "1",
        "itens": "100"
      },
      "saveAs": "despesas"
    }
  ],
  "finalResult": "despesas"
}

Usuário: Procure proposições sobre escala 6x1
Resposta:
{
  "tool": "search_proposicoes",
  "arguments": {
    "keywords": "escala 6x1",
    "pagina": "{DEFAULT_PAGE}",
    "itens": "{DEFAULT_ITEMS}"
  }
}

Usuário: Tramitação do PL 2630 de 2020
Resposta:
{
  "steps": [
    {
      "tool": "search_proposicoes",
      "arguments": {
        "siglaTipo": "PL",
        "numero": "2630",
        "ano": "2020",
        "pagina": "1",
        "itens": "1"
      },
      "saveAs": "proposicao"
    },
    {
      "tool": "get_proposicao_tramitacoes",
      "arguments": {
        "idProposicao": "{{proposicao.dados[0].id}}"
      },
      "saveAs": "tramitacoes"
    }
  ],
  "finalResult": "tramitacoes"
}
"""
            .Replace("{DEFAULT_PAGE}", defaultPage.ToString(CultureInfo.InvariantCulture))
            .Replace("{DEFAULT_ITEMS}", defaultItems.ToString(CultureInfo.InvariantCulture));
    }

    private Dictionary<string, string?> NormalizeArguments(
        string tool,
        IReadOnlyDictionary<string, string?> arguments,
        int defaultPage,
        int defaultItems)
    {
        if (!AllowedArguments.TryGetValue(tool, out var allowedArguments))
        {
            throw new InvalidOperationException($"Tool não suportada pelo planejador: {tool}");
        }

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in arguments)
        {
            var name = NormalizeArgumentName(argument.Key);
            if (!allowedArguments.Contains(name) || string.IsNullOrWhiteSpace(argument.Value))
            {
                continue;
            }

            result[name] = argument.Value!.Trim();
        }

        if (allowedArguments.Contains("pagina"))
        {
            result.TryAdd("pagina", defaultPage.ToString(CultureInfo.InvariantCulture));
        }

        if (allowedArguments.Contains("itens"))
        {
            result.TryAdd("itens", defaultItems.ToString(CultureInfo.InvariantCulture));
        }

        if (result.TryGetValue("itens", out var rawItems)
            && int.TryParse(rawItems, NumberStyles.Integer, CultureInfo.InvariantCulture, out var items))
        {
            result["itens"] = Math.Clamp(items, 1, 100).ToString(CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static Dictionary<string, string?> ReadArguments(JsonElement element)
    {
        var arguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!element.TryGetProperty("arguments", out var argumentsElement) || argumentsElement.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        foreach (var property in argumentsElement.EnumerateObject())
        {
            var value = ConvertJsonElementToString(property.Value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                arguments[property.Name] = value;
            }
        }

        return arguments;
    }

    private static string ConvertJsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };
    }

    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{', StringComparison.Ordinal);
        var end = content.LastIndexOf('}', StringComparison.Ordinal);

        if (start < 0 || end <= start)
        {
            throw new JsonException("A resposta do modelo não contém objeto JSON.");
        }

        return content[start..(end + 1)];
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? throw new InvalidOperationException($"A propriedade '{propertyName}' está vazia.")
            : throw new InvalidOperationException($"A propriedade obrigatória '{propertyName}' não foi retornada.");
    }

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string NormalizeToolName(string tool)
    {
        return tool.Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            "buscar_deputados" or "listar_deputados" or "deputados" => "search_deputados",
            "deputado" or "detalhe_deputado" => "get_deputado",
            "despesas_deputado" or "gastos_deputado" => "get_deputado_despesas",
            "buscar_proposicoes" or "listar_proposicoes" or "proposicoes" => "search_proposicoes",
            "proposicao" or "detalhe_proposicao" => "get_proposicao",
            "votacoes" or "buscar_votacoes" => "search_votacoes",
            "votos_votacao" => "get_votacao_votos",
            "orientacoes_votacao" => "get_votacao_orientacoes",
            var value => value
        };
    }

    private static string NormalizeArgumentName(string argumentName)
    {
        return argumentName.Trim() switch
        {
            "id" => "idDeputado",
            "uf" => "siglaUf",
            "partido" => "siglaPartido",
            "tipo" => "siglaTipo",
            "numeroProposicao" => "numero",
            "anoProposicao" => "ano",
            "termo" or "tema" or "assunto" => "keywords",
            "page" => "pagina",
            "items" or "limit" => "itens",
            var value => value
        };
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
        var rawValue = GetString(configurationKey, environmentKey, defaultValue.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, min, max) : defaultValue;
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

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private static HashSet<string> Args(params string[] values) => new(values, StringComparer.OrdinalIgnoreCase);

    private sealed record PlannedRequest(ToolPlan? ToolPlan, ChatExecutionPlan? ExecutionPlan);
}
