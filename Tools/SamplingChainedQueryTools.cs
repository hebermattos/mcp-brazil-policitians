using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpBrazilPoliticians.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpBrazilPoliticians.Tools;

[McpServerToolType]
public static class SamplingChainedQueryTools
{
    private const int DefaultCandidateItems = 10;
    private const int MaxPromptJsonChars = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    [McpServerTool(Name = "consultar_deputado_com_sampling"), Description("Uses MCP sampling to plan and execute chained Câmara API calls about a federal deputy. It first samples a plan, searches the deputy, samples the best candidate, then calls the correct deputy sub-resource.")]
    public static async Task<string> ConsultarDeputadoComSamplingAsync(
        McpServer server,
        [Description("Natural language question about a Brazilian federal deputy. Example: 'Mostre as despesas da Erika Hilton em 2026'.")] string pergunta,
        [Description("Page number for the final Câmara API call. Starts at 1.")] int pagina = 1,
        [Description("Items per page for the final Câmara API call. Recommended: 10 to 50.")] int itens = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pergunta))
        {
            throw new ArgumentException("A pergunta é obrigatória.", nameof(pergunta));
        }

        pagina = Math.Max(1, pagina);
        itens = Math.Clamp(itens, 1, 100);

        var plan = await SampleJsonAsync<DeputadoSamplingPlan>(
            server,
            BuildPlanPrompt(pergunta, pagina, itens),
            "Você planeja chamadas para a API de Dados Abertos da Câmara. Retorne somente JSON válido.",
            maxTokens: 700,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(plan.NomeDeputado))
        {
            return JsonSerializer.Serialize(new SamplingChainedQueryResult(
                Pergunta: pergunta,
                SamplingUsed: true,
                Status: "missing_deputy_name",
                Message: "Não foi possível identificar o nome do deputado na pergunta.",
                Plan: plan,
                Selection: null,
                ExecutedSteps: [],
                Answer: null,
                Data: null), JsonOptions);
        }

        var searchArguments = new Dictionary<string, object?>
        {
            ["nome"] = plan.NomeDeputado,
            ["pagina"] = 1,
            ["itens"] = DefaultCandidateItems,
            ["ordem"] = "ASC",
            ["ordenarPor"] = "nome"
        };

        var searchJson = await CamaraApiClient.GetJsonAsync("deputados", searchArguments, cancellationToken);
        var searchStep = new SamplingChainedQueryStep(
            Tool: "search_deputados",
            Path: "deputados",
            Arguments: ToStringDictionary(searchArguments),
            Data: ParseJsonElement(searchJson));

        var selection = await SampleJsonAsync<DeputadoSamplingSelection>(
            server,
            BuildSelectionPrompt(pergunta, plan, searchJson),
            "Você escolhe o identificador oficial correto da Câmara com base nos candidatos retornados. Retorne somente JSON válido.",
            maxTokens: 700,
            cancellationToken);

        if (selection.IsAmbiguous || selection.IdDeputado is null)
        {
            return JsonSerializer.Serialize(new SamplingChainedQueryResult(
                Pergunta: pergunta,
                SamplingUsed: true,
                Status: "ambiguous_or_not_found",
                Message: "O sampling não conseguiu escolher um deputado com segurança. Use os candidatos retornados para refinar a pergunta.",
                Plan: plan,
                Selection: selection,
                ExecutedSteps: [searchStep],
                Answer: null,
                Data: searchStep.Data), JsonOptions);
        }

        var finalCall = BuildFinalCall(selection.IdDeputado.Value, plan, pagina, itens);
        var finalJson = await CamaraApiClient.GetJsonAsync(finalCall.Path, finalCall.Arguments, cancellationToken);
        var finalStep = new SamplingChainedQueryStep(
            Tool: finalCall.Tool,
            Path: finalCall.Path,
            Arguments: ToStringDictionary(finalCall.Arguments),
            Data: ParseJsonElement(finalJson));

        var answer = await SampleTextAsync(
            server,
            BuildAnswerPrompt(pergunta, plan, selection, finalCall.Tool, finalJson),
            "Você responde em português usando apenas os dados fornecidos pela API da Câmara. Não invente informações.",
            maxTokens: 1_200,
            cancellationToken);

        return JsonSerializer.Serialize(new SamplingChainedQueryResult(
            Pergunta: pergunta,
            SamplingUsed: true,
            Status: "ok",
            Message: "Consulta encadeada executada com sampling.",
            Plan: plan,
            Selection: selection,
            ExecutedSteps: [searchStep, finalStep],
            Answer: answer,
            Data: finalStep.Data), JsonOptions);
    }

    private static FinalCamaraCall BuildFinalCall(int idDeputado, DeputadoSamplingPlan plan, int pagina, int itens)
    {
        return plan.Action switch
        {
            DeputadoSamplingAction.Despesas => new FinalCamaraCall(
                Tool: "get_deputado_despesas",
                Path: $"deputados/{idDeputado}/despesas",
                Arguments: RemoveNullValues(new Dictionary<string, object?>
                {
                    ["ano"] = plan.Ano,
                    ["mes"] = plan.Mes,
                    ["pagina"] = pagina,
                    ["itens"] = itens,
                    ["ordem"] = "ASC",
                    ["ordenarPor"] = "dataDocumento"
                })),

            DeputadoSamplingAction.Discursos => new FinalCamaraCall(
                Tool: "get_deputado_discursos",
                Path: $"deputados/{idDeputado}/discursos",
                Arguments: RemoveNullValues(new Dictionary<string, object?>
                {
                    ["dataInicio"] = plan.DataInicio,
                    ["dataFim"] = plan.DataFim,
                    ["pagina"] = pagina,
                    ["itens"] = itens
                })),

            DeputadoSamplingAction.Eventos => new FinalCamaraCall(
                Tool: "get_deputado_eventos",
                Path: $"deputados/{idDeputado}/eventos",
                Arguments: RemoveNullValues(new Dictionary<string, object?>
                {
                    ["dataInicio"] = plan.DataInicio,
                    ["dataFim"] = plan.DataFim,
                    ["pagina"] = pagina,
                    ["itens"] = itens,
                    ["ordem"] = "ASC",
                    ["ordenarPor"] = "dataHoraInicio"
                })),

            DeputadoSamplingAction.Frentes => new FinalCamaraCall(
                Tool: "get_deputado_frentes",
                Path: $"deputados/{idDeputado}/frentes",
                Arguments: RemoveNullValues(new Dictionary<string, object?>
                {
                    ["pagina"] = pagina,
                    ["itens"] = itens
                })),

            DeputadoSamplingAction.Orgaos => new FinalCamaraCall(
                Tool: "get_deputado_orgaos",
                Path: $"deputados/{idDeputado}/orgaos",
                Arguments: RemoveNullValues(new Dictionary<string, object?>
                {
                    ["pagina"] = pagina,
                    ["itens"] = itens
                })),

            DeputadoSamplingAction.Profissoes => new FinalCamaraCall(
                Tool: "get_deputado_profissoes",
                Path: $"deputados/{idDeputado}/profissoes",
                Arguments: new Dictionary<string, object?>()),

            DeputadoSamplingAction.Votacoes => new FinalCamaraCall(
                Tool: "get_deputado_votacoes",
                Path: $"deputados/{idDeputado}/votacoes",
                Arguments: RemoveNullValues(new Dictionary<string, object?>
                {
                    ["pagina"] = pagina,
                    ["itens"] = itens
                })),

            DeputadoSamplingAction.HistoricoPartidos => new FinalCamaraCall(
                Tool: "get_deputado_historico_partidos",
                Path: $"deputados/{idDeputado}/historico",
                Arguments: new Dictionary<string, object?>()),

            _ => new FinalCamaraCall(
                Tool: "get_deputado",
                Path: $"deputados/{idDeputado}",
                Arguments: new Dictionary<string, object?>())
        };
    }

    private static string BuildPlanPrompt(string pergunta, int pagina, int itens)
    {
        return $$"""
        Extraia da pergunta uma intenção de consulta sobre deputado federal.

        Pergunta:
        {{pergunta}}

        Retorne somente JSON válido, sem markdown, neste formato:
        {
          "nomeDeputado": "nome mencionado ou null",
          "action": "detalhes",
          "ano": null,
          "mes": null,
          "dataInicio": null,
          "dataFim": null,
          "pagina": {{pagina}},
          "itens": {{itens}},
          "reason": "justificativa curta"
        }

        Valores permitidos para action:
        - detalhes: dados cadastrais ou perfil do deputado
        - despesas: gastos, despesas, cota parlamentar, reembolsos
        - discursos: discursos, falas, pronunciamentos
        - eventos: agenda, reuniões, eventos
        - frentes: frentes parlamentares
        - orgaos: órgãos, comissões, colegiados
        - profissoes: profissões declaradas
        - votacoes: votações associadas ao deputado
        - historicoPartidos: histórico partidário

        Regras:
        - Não invente idDeputado.
        - Não use apelidos como id.
        - Se houver ano ou mês explícito, preencha ano e mes.
        - Datas devem ficar no formato yyyy-MM-dd quando existirem.
        - Se não houver nome de deputado, use null em nomeDeputado.
        """;
    }

    private static string BuildSelectionPrompt(string pergunta, DeputadoSamplingPlan plan, string searchJson)
    {
        return $$"""
        Escolha o deputado correto usando somente os candidatos retornados pela API da Câmara.

        Pergunta original:
        {{pergunta}}

        Plano:
        {{JsonSerializer.Serialize(plan, JsonOptions)}}

        Resultado de search_deputados:
        {{TruncateForPrompt(searchJson)}}

        Retorne somente JSON válido, sem markdown, neste formato:
        {
          "idDeputado": 123,
          "nome": "Nome do deputado escolhido",
          "siglaPartido": "PT",
          "siglaUf": "SP",
          "isAmbiguous": false,
          "reason": "justificativa curta"
        }

        Regras:
        - Use apenas ids presentes no JSON de candidatos.
        - Se não houver candidato, idDeputado deve ser null e isAmbiguous deve ser true.
        - Se houver múltiplos candidatos plausíveis sem evidência suficiente, idDeputado deve ser null e isAmbiguous deve ser true.
        - Considere nome, partido, UF e situação quando disponíveis.
        """;
    }

    private static string BuildAnswerPrompt(
        string pergunta,
        DeputadoSamplingPlan plan,
        DeputadoSamplingSelection selection,
        string tool,
        string finalJson)
    {
        return $$"""
        Responda em português de forma objetiva.

        Pergunta original:
        {{pergunta}}

        Plano usado:
        {{JsonSerializer.Serialize(plan, JsonOptions)}}

        Deputado escolhido:
        {{JsonSerializer.Serialize(selection, JsonOptions)}}

        Tool lógica executada:
        {{tool}}

        JSON retornado pela API da Câmara:
        {{TruncateForPrompt(finalJson)}}

        Regras:
        - Use apenas os dados do JSON.
        - Não invente valores ausentes.
        - Quando o JSON não trouxer dados suficientes, diga claramente.
        - Para listas, mostre os principais itens retornados e respeite a paginação.
        """;
    }

    private static async Task<T> SampleJsonAsync<T>(
        McpServer server,
        string prompt,
        string systemPrompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var text = await SampleTextAsync(server, prompt, systemPrompt, maxTokens, cancellationToken);
        var json = ExtractJsonObject(text);

        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("O sampling retornou JSON vazio ou incompatível.");
    }

    private static async Task<string> SampleTextAsync(
        McpServer server,
        string prompt,
        string systemPrompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var samplingParams = new CreateMessageRequestParams
        {
            Messages =
            [
                new SamplingMessage
                {
                    Role = Role.User,
                    Content = [new TextContentBlock { Text = prompt }]
                }
            ],
            SystemPrompt = systemPrompt,
            MaxTokens = maxTokens,
            Temperature = 0.2f,
            IncludeContext = ContextInclusion.ThisServer
        };

        try
        {
            var sampleResult = await server.SampleAsync(samplingParams, cancellationToken: cancellationToken);
            var text = sampleResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("O sampling retornou texto vazio.");
            }

            return text.Trim();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "O cliente MCP conectado não suporta sampling ou recusou a chamada sampling/createMessage. Use um cliente MCP com suporte a sampling para executar esta tool.",
                ex);
        }
    }

    private static string ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].Trim();
        }
        else if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[3..].Trim();
        }

        if (trimmed.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].Trim();
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');

        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            throw new InvalidOperationException($"O sampling não retornou JSON válido: {text}");
        }

        return trimmed[firstBrace..(lastBrace + 1)];
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, object?> RemoveNullValues(IReadOnlyDictionary<string, object?> arguments)
    {
        return arguments
            .Where(item => item.Value is not null && !string.IsNullOrWhiteSpace(item.Value.ToString()))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> ToStringDictionary(IReadOnlyDictionary<string, object?> arguments)
    {
        return arguments.ToDictionary(
            item => item.Key,
            item => item.Value?.ToString(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string TruncateForPrompt(string value)
    {
        if (value.Length <= MaxPromptJsonChars)
        {
            return value;
        }

        return value[..MaxPromptJsonChars] + "\n... JSON truncado para caber no prompt de sampling ...";
    }

    private sealed record FinalCamaraCall(
        string Tool,
        string Path,
        IReadOnlyDictionary<string, object?> Arguments);

    private sealed record SamplingChainedQueryResult(
        string Pergunta,
        bool SamplingUsed,
        string Status,
        string Message,
        DeputadoSamplingPlan? Plan,
        DeputadoSamplingSelection? Selection,
        IReadOnlyList<SamplingChainedQueryStep> ExecutedSteps,
        string? Answer,
        JsonElement? Data);

    private sealed record SamplingChainedQueryStep(
        string Tool,
        string Path,
        IReadOnlyDictionary<string, string?> Arguments,
        JsonElement Data);

    private sealed record DeputadoSamplingPlan(
        string? NomeDeputado,
        DeputadoSamplingAction Action,
        int? Ano,
        int? Mes,
        string? DataInicio,
        string? DataFim,
        int Pagina,
        int Itens,
        string? Reason);

    private sealed record DeputadoSamplingSelection(
        int? IdDeputado,
        string? Nome,
        string? SiglaPartido,
        string? SiglaUf,
        bool IsAmbiguous,
        string? Reason);

    [JsonConverter(typeof(JsonStringEnumConverter<DeputadoSamplingAction>))]
    private enum DeputadoSamplingAction
    {
        Detalhes,
        Despesas,
        Discursos,
        Eventos,
        Frentes,
        Orgaos,
        Profissoes,
        Votacoes,
        HistoricoPartidos
    }
}
