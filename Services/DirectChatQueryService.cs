using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class DirectChatQueryService
{
    private static readonly Regex PropositionsByDeputyRegex = new(
        @"\b(?:proposi[cç][oõ]es|projetos?)\s+(?:d[ao]s?\s+)?(?:deputad[ao]|parlamentar)\s+(?<author>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExpensesByDeputyRegex = new(
        @"\b(?:despesas?|gastos?|cotas?|reembolsos?)\s+(?:d[ao]s?\s+)?(?:deputad[ao]|parlamentar)\s+(?<name>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VotesByDeputyRegex = new(
        @"\b(?:vota[cç][oõ]es|votos?|votou|favor|contra)\b.*\b(?:d[ao]s?\s+)?(?:deputad[ao]|parlamentar)\s+(?<name>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ChatPlanExecutorService _planExecutorService;
    private readonly CamaraToolExecutionService _toolExecutionService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DirectChatQueryService> _logger;

    public DirectChatQueryService(
        ChatPlanExecutorService planExecutorService,
        CamaraToolExecutionService toolExecutionService,
        IConfiguration configuration,
        ILogger<DirectChatQueryService> logger)
    {
        _planExecutorService = planExecutorService;
        _toolExecutionService = toolExecutionService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ChatPromptResponse?> TryHandleAsync(string prompt, CancellationToken cancellationToken)
    {
        if (LooksLikeVotesFromLatestVotingQuery(prompt))
        {
            return await GetVotesFromLatestVotingsAsync(prompt, cancellationToken, defaultVotingCount: 1);
        }

        if (LooksLikeVotesByDeputyFromLatestVotingsQuery(prompt))
        {
            return await GetVotesFromLatestVotingsAsync(prompt, cancellationToken, defaultVotingCount: 5);
        }

        var plan = TryCreateDirectPlan(prompt);
        if (plan is null)
        {
            return null;
        }

        _logger.LogInformation(
            "Executing direct chat query through generic chained plan executor. Steps={StepCount}, FinalResult={FinalResult}",
            plan.Steps.Count,
            plan.FinalResult);

        var executedPlan = await _planExecutorService.ExecuteAsync(plan, cancellationToken);

        return new ChatPromptResponse(
            "Consulta direta executada por plano encadeado.",
            executedPlan.FinalTool,
            executedPlan.FinalArguments,
            executedPlan.FinalData,
            null);
    }

    private async Task<ChatPromptResponse> GetVotesFromLatestVotingsAsync(string prompt, CancellationToken cancellationToken, int defaultVotingCount)
    {
        var votingCount = ExtractRequestedCount(prompt, defaultValue: defaultVotingCount, min: 1, max: 10);
        var maxVotesPerVoting = GetInt("Chat:DefaultVoteItems", "CHAT_DEFAULT_VOTE_ITEMS", 100, 1, 100);

        var searchArguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["pagina"] = "1",
            ["itens"] = votingCount.ToString(CultureInfo.InvariantCulture),
            ["ordem"] = "DESC",
            ["ordenarPor"] = "dataHoraRegistro"
        };

        _logger.LogInformation(
            "Executing fan-out query for votes from latest votings. VotingCount={VotingCount}, MaxVotesPerVoting={MaxVotesPerVoting}",
            votingCount,
            maxVotesPerVoting);

        using var votingsJson = await _toolExecutionService.ExecuteAsync("search_votacoes", searchArguments, cancellationToken);
        var combinedVotes = new JsonArray();
        var votingSummaries = new JsonArray();

        if (votingsJson.RootElement.TryGetProperty("dados", out var votings) && votings.ValueKind == JsonValueKind.Array)
        {
            foreach (var voting in votings.EnumerateArray())
            {
                var votingId = GetString(voting, "id");
                if (string.IsNullOrWhiteSpace(votingId))
                {
                    continue;
                }

                var votingDescription = GetString(voting, "descricao") ?? GetString(voting, "titulo");
                var votingDate = GetString(voting, "dataHoraRegistro") ?? GetString(voting, "dataHora") ?? GetString(voting, "data");
                var votingOrg = GetString(voting, "siglaOrgao") ?? GetNestedString(voting, "orgao", "sigla");

                votingSummaries.Add(new JsonObject
                {
                    ["idVotacao"] = votingId,
                    ["dataVotacao"] = votingDate,
                    ["siglaOrgao"] = votingOrg,
                    ["descricao"] = votingDescription
                });

                using var votesJson = await _toolExecutionService.ExecuteAsync(
                    "get_votacao_votos",
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["idVotacao"] = votingId,
                        ["pagina"] = "1",
                        ["itens"] = maxVotesPerVoting.ToString(CultureInfo.InvariantCulture)
                    },
                    cancellationToken);

                if (!votesJson.RootElement.TryGetProperty("dados", out var votes) || votes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var vote in votes.EnumerateArray())
                {
                    var parsedVote = JsonNode.Parse(vote.GetRawText());
                    var voteNode = parsedVote as JsonObject ?? new JsonObject();
                    voteNode["idVotacaoOrigem"] = votingId;
                    voteNode["dataVotacaoOrigem"] = votingDate;
                    voteNode["siglaOrgaoOrigem"] = votingOrg;
                    voteNode["descricaoVotacaoOrigem"] = votingDescription;
                    combinedVotes.Add(voteNode);
                }
            }
        }

        var root = new JsonObject
        {
            ["dados"] = combinedVotes,
            ["votacoesConsultadas"] = votingSummaries
        };

        using var combinedJson = JsonDocument.Parse(root.ToJsonString());
        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["quantidadeVotacoes"] = votingCount.ToString(CultureInfo.InvariantCulture),
            ["itensPorVotacao"] = maxVotesPerVoting.ToString(CultureInfo.InvariantCulture),
            ["ordem"] = "DESC",
            ["ordenarPor"] = "dataHoraRegistro"
        };

        return new ChatPromptResponse(
            "Consulta direta de votos das últimas votações.",
            "get_votacao_votos",
            arguments,
            combinedJson.RootElement.Clone(),
            null);
    }

    private ChatExecutionPlan? TryCreateDirectPlan(string prompt)
    {
        if (LooksLikeLatestVotingQuery(prompt))
        {
            return CreateLatestVotingPlan();
        }

        var deputyNameForVotes = TryExtractDeputyNameForVotes(prompt);
        if (!string.IsNullOrWhiteSpace(deputyNameForVotes))
        {
            return CreateDeputyVotesPlan(deputyNameForVotes);
        }

        var deputyNameForExpenses = TryExtractDeputyNameForExpenses(prompt);
        if (!string.IsNullOrWhiteSpace(deputyNameForExpenses))
        {
            return CreateDeputyExpensesPlan(deputyNameForExpenses);
        }

        var author = TryExtractPropositionsAuthor(prompt);
        if (!string.IsNullOrWhiteSpace(author))
        {
            return CreatePropositionsByAuthorPlan(author);
        }

        return null;
    }

    private static ChatExecutionPlan CreateLatestVotingPlan()
    {
        return new ChatExecutionPlan(
            Steps:
            [
                new ChatExecutionStep(
                    Tool: "search_votacoes",
                    Arguments: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["pagina"] = "1",
                        ["itens"] = "1",
                        ["ordem"] = "DESC",
                        ["ordenarPor"] = "dataHoraRegistro"
                    },
                    SaveAs: "votacoes")
            ],
            FinalResult: "votacoes");
    }

    private ChatExecutionPlan CreateDeputyVotesPlan(string deputyName)
    {
        var page = GetInt("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", 1, 1, 10000);
        var items = GetInt("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", 10, 1, 100);

        return new ChatExecutionPlan(
            Steps:
            [
                new ChatExecutionStep(
                    Tool: "search_deputados",
                    Arguments: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["nome"] = deputyName,
                        ["pagina"] = "1",
                        ["itens"] = "1"
                    },
                    SaveAs: "deputado"),
                new ChatExecutionStep(
                    Tool: "get_deputado_votacoes",
                    Arguments: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["idDeputado"] = "{{deputado.dados[0].id}}",
                        ["nome"] = "{{deputado.dados[0].nome}}",
                        ["pagina"] = page.ToString(CultureInfo.InvariantCulture),
                        ["itens"] = items.ToString(CultureInfo.InvariantCulture)
                    },
                    SaveAs: "votacoes")
            ],
            FinalResult: "votacoes");
    }

    private ChatExecutionPlan CreateDeputyExpensesPlan(string deputyName)
    {
        var page = GetInt("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", 1, 1, 10000);
        var items = GetInt("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", 10, 1, 100);

        return new ChatExecutionPlan(
            Steps:
            [
                new ChatExecutionStep(
                    Tool: "search_deputados",
                    Arguments: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["nome"] = deputyName,
                        ["pagina"] = "1",
                        ["itens"] = "1"
                    },
                    SaveAs: "deputado"),
                new ChatExecutionStep(
                    Tool: "get_deputado_despesas",
                    Arguments: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["idDeputado"] = "{{deputado.dados[0].id}}",
                        ["nome"] = "{{deputado.dados[0].nome}}",
                        ["pagina"] = page.ToString(CultureInfo.InvariantCulture),
                        ["itens"] = items.ToString(CultureInfo.InvariantCulture)
                    },
                    SaveAs: "despesas")
            ],
            FinalResult: "despesas");
    }

    private ChatExecutionPlan CreatePropositionsByAuthorPlan(string author)
    {
        var page = GetInt("Chat:DefaultSearchPage", "CHAT_DEFAULT_SEARCH_PAGE", 1, 1, 10000);
        var items = GetInt("Chat:DefaultSearchItems", "CHAT_DEFAULT_SEARCH_ITEMS", 10, 1, 100);

        return new ChatExecutionPlan(
            Steps:
            [
                new ChatExecutionStep(
                    Tool: "search_proposicoes",
                    Arguments: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["autor"] = author,
                        ["pagina"] = page.ToString(CultureInfo.InvariantCulture),
                        ["itens"] = items.ToString(CultureInfo.InvariantCulture)
                    },
                    SaveAs: "proposicoes")
            ],
            FinalResult: "proposicoes");
    }

    private static bool LooksLikeVotesFromLatestVotingQuery(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var normalized = NormalizeForMatching(prompt);
        var mentionsVotes = normalized.Contains("votos", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("voto", StringComparison.OrdinalIgnoreCase);
        var mentionsVoting = normalized.Contains("votacao", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("votacoes", StringComparison.OrdinalIgnoreCase);
        var mentionsLatest = ContainsLatestWord(normalized);

        return mentionsVotes && mentionsVoting && mentionsLatest;
    }

    private static bool LooksLikeVotesByDeputyFromLatestVotingsQuery(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var normalized = NormalizeForMatching(prompt);
        var mentionsVotes = normalized.Contains("votos", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("voto", StringComparison.OrdinalIgnoreCase);
        var mentionsDeputy = normalized.Contains("deputado", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("deputados", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("parlamentar", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("parlamentares", StringComparison.OrdinalIgnoreCase);
        var mentionsLatestVotings = ContainsLatestWord(normalized);
        var mentionsVotings = normalized.Contains("votacoes", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("votacao", StringComparison.OrdinalIgnoreCase);

        return mentionsVotes && mentionsDeputy && mentionsLatestVotings && mentionsVotings;
    }

    private static bool LooksLikeLatestVotingQuery(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        var normalized = NormalizeForMatching(prompt);
        var mentionsVoting = normalized.Contains("votacao", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("votacoes", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("voto", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("votos", StringComparison.OrdinalIgnoreCase);

        var mentionsLatest = ContainsLatestWord(normalized);

        return mentionsVoting && mentionsLatest;
    }

    private static bool ContainsLatestWord(string normalized)
    {
        return normalized.Contains("ultima", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ultimas", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ultimo", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ultimos", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ulitma", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ulitmas", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ultma", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ultmas", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("recente", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("recentes", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("mais recente", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("mais recentes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractPropositionsAuthor(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var normalizedPrompt = Regex.Replace(prompt.Trim(), "\\s+", " ");
        var match = PropositionsByDeputyRegex.Match(normalizedPrompt);
        if (!match.Success)
        {
            return null;
        }

        return CleanPersonName(match.Groups["author"].Value);
    }

    private static string? TryExtractDeputyNameForExpenses(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var normalizedPrompt = Regex.Replace(prompt.Trim(), "\\s+", " ");
        var match = ExpensesByDeputyRegex.Match(normalizedPrompt);
        if (!match.Success)
        {
            return null;
        }

        return CleanPersonName(match.Groups["name"].Value);
    }

    private static string? TryExtractDeputyNameForVotes(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        var normalizedPrompt = Regex.Replace(prompt.Trim(), "\\s+", " ");
        var match = VotesByDeputyRegex.Match(normalizedPrompt);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value;
        name = Regex.Replace(name, @"\b(?:se\s+)?votou\s+a\s+favor\s+ou\s+contra\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        name = Regex.Replace(name, @"\b(?:a\s+favor|contra)\b.*$", string.Empty, RegexOptions.IgnoreCase).Trim();

        return CleanPersonName(name);
    }

    private static string? CleanPersonName(string value)
    {
        var name = value.Trim();
        name = Regex.Replace(name, @"[?.!,;:]+$", string.Empty).Trim();
        name = Regex.Replace(name, @"\b(?:por favor|pfv|pra mim|para mim)$", string.Empty, RegexOptions.IgnoreCase).Trim();

        return string.IsNullOrWhiteSpace(name) ? null : ToTitleCase(name);
    }

    private int GetInt(string configurationKey, string environmentKey, int defaultValue, int min, int max)
    {
        var configurationValue = _configuration[configurationKey];
        var environmentValue = Environment.GetEnvironmentVariable(environmentKey);
        var rawValue = string.IsNullOrWhiteSpace(configurationValue) ? environmentValue : configurationValue;

        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : defaultValue;
    }

    private static int ExtractRequestedCount(string prompt, int defaultValue, int min, int max)
    {
        var match = Regex.Match(prompt, @"\b(?<count>\d{1,2})\b");
        if (!match.Success || !int.TryParse(match.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return defaultValue;
        }

        return Math.Clamp(count, min, max);
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string? GetNestedString(JsonElement item, string objectName, string propertyName)
    {
        return item.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, propertyName)
            : null;
    }

    private static string ToTitleCase(string value)
    {
        var textInfo = CultureInfo.GetCultureInfo("pt-BR").TextInfo;
        return textInfo.ToTitleCase(value.ToLower(CultureInfo.GetCultureInfo("pt-BR")));
    }

    private static string NormalizeForMatching(string value)
    {
        var normalized = value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return string.Concat(normalized.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark));
    }
}
