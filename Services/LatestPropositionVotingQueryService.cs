using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class LatestPropositionVotingQueryService
{
    private readonly CamaraToolExecutionService _toolExecutionService;
    private readonly ILogger<LatestPropositionVotingQueryService> _logger;

    public LatestPropositionVotingQueryService(
        CamaraToolExecutionService toolExecutionService,
        ILogger<LatestPropositionVotingQueryService> logger)
    {
        _toolExecutionService = toolExecutionService;
        _logger = logger;
    }

    public async Task<ChatPromptResponse?> TryHandleAsync(string prompt, CancellationToken cancellationToken)
    {
        if (!LooksLikeVotesFromLatestPropositionVoting(prompt))
        {
            return null;
        }

        _logger.LogInformation("Executing deterministic latest proposition voting query. Prompt={Prompt}", prompt);

        using var propositionsJson = await _toolExecutionService.ExecuteAsync(
            "search_proposicoes",
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["pagina"] = "1",
                ["itens"] = "1",
                ["ordem"] = "DESC",
                ["ordenarPor"] = "dataApresentacao"
            },
            cancellationToken);

        var proposition = GetFirstDataItem(propositionsJson.RootElement);
        if (proposition is null)
        {
            return EmptyResponse("Não encontrei proposições para consultar votação.");
        }

        var propositionId = GetString(proposition.Value, "id");
        if (string.IsNullOrWhiteSpace(propositionId))
        {
            return EmptyResponse("A última proposição retornada pela API não possui ID.");
        }

        using var propositionVotingsJson = await _toolExecutionService.ExecuteAsync(
            "get_proposicao_votacoes",
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["idProposicao"] = propositionId
            },
            cancellationToken);

        var voting = GetFirstDataItem(propositionVotingsJson.RootElement);
        if (voting is null)
        {
            return EmptyResponse($"A proposição {propositionId} não possui votações retornadas pela API.", propositionId);
        }

        var votingId = GetString(voting.Value, "id") ?? GetString(voting.Value, "idVotacao");
        if (string.IsNullOrWhiteSpace(votingId))
        {
            return EmptyResponse($"A votação da proposição {propositionId} não possui ID retornado pela API.", propositionId);
        }

        using var votesJson = await _toolExecutionService.ExecuteAsync(
            "get_votacao_votos",
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["idVotacao"] = votingId,
                ["pagina"] = "1",
                ["itens"] = "100"
            },
            cancellationToken);

        var enrichedVotes = new JsonArray();
        if (votesJson.RootElement.TryGetProperty("dados", out var votes) && votes.ValueKind == JsonValueKind.Array)
        {
            foreach (var vote in votes.EnumerateArray())
            {
                var parsedVote = JsonNode.Parse(vote.GetRawText());
                var voteNode = parsedVote as JsonObject ?? new JsonObject();
                voteNode["idProposicaoOrigem"] = propositionId;
                voteNode["siglaTipoProposicaoOrigem"] = GetString(proposition.Value, "siglaTipo");
                voteNode["numeroProposicaoOrigem"] = GetString(proposition.Value, "numero");
                voteNode["anoProposicaoOrigem"] = GetString(proposition.Value, "ano");
                voteNode["ementaProposicaoOrigem"] = GetString(proposition.Value, "ementa");
                voteNode["idVotacaoOrigem"] = votingId;
                voteNode["dataVotacaoOrigem"] = GetString(voting.Value, "dataHoraRegistro") ?? GetString(voting.Value, "dataHora") ?? GetString(voting.Value, "data");
                voteNode["descricaoVotacaoOrigem"] = GetString(voting.Value, "descricao") ?? GetString(voting.Value, "titulo");
                enrichedVotes.Add(voteNode);
            }
        }

        var root = new JsonObject
        {
            ["dados"] = enrichedVotes,
            ["proposicaoConsultada"] = JsonNode.Parse(proposition.Value.GetRawText()),
            ["votacaoConsultada"] = JsonNode.Parse(voting.Value.GetRawText())
        };

        using var resultJson = JsonDocument.Parse(root.ToJsonString());
        return new ChatPromptResponse(
            "Consulta direta de votos da votação da última proposição.",
            "get_votacao_votos",
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["idProposicao"] = propositionId,
                ["idVotacao"] = votingId,
                ["pagina"] = "1",
                ["itens"] = "100"
            },
            resultJson.RootElement.Clone(),
            null);
    }

    private static bool LooksLikeVotesFromLatestPropositionVoting(string prompt)
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
        var mentionsVoting = normalized.Contains("votacao", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("votacoes", StringComparison.OrdinalIgnoreCase);
        var mentionsLatestProposition = normalized.Contains("ultima proposicao", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ultima proposta", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ultimo projeto", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("proposicao mais recente", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("projeto mais recente", StringComparison.OrdinalIgnoreCase);

        return mentionsVotes && mentionsDeputy && mentionsVoting && mentionsLatestProposition;
    }

    private static JsonElement? GetFirstDataItem(JsonElement root)
    {
        if (!root.TryGetProperty("dados", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
        {
            return null;
        }

        return data[0].Clone();
    }

    private static ChatPromptResponse EmptyResponse(string message, string? propositionId = null)
    {
        using var json = JsonDocument.Parse("{\"dados\":[]}");
        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(propositionId))
        {
            arguments["idProposicao"] = propositionId;
        }

        return new ChatPromptResponse(message, "get_votacao_votos", arguments, json.RootElement.Clone(), null);
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

    private static string NormalizeForMatching(string value)
    {
        var normalized = value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        return string.Concat(normalized.Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark));
    }
}
