using System.Globalization;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<DirectChatQueryService> _logger;

    public DirectChatQueryService(
        ChatPlanExecutorService planExecutorService,
        IConfiguration configuration,
        ILogger<DirectChatQueryService> logger)
    {
        _planExecutorService = planExecutorService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<ChatPromptResponse?> TryHandleAsync(string prompt, CancellationToken cancellationToken)
    {
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

        var mentionsLatest = normalized.Contains("ultima", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ultimas", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("recente", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("recentes", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("mais recente", StringComparison.OrdinalIgnoreCase);

        return mentionsVoting && mentionsLatest;
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
