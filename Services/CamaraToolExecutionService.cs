using System.Text.Json;
using System.Text.RegularExpressions;

namespace McpBrazilPoliticians.Services;

public sealed class CamaraToolExecutionService
{
    private readonly CamaraApiClient _camaraApiClient;
    private readonly ILogger<CamaraToolExecutionService> _logger;

    public CamaraToolExecutionService(
        CamaraApiClient camaraApiClient,
        ILogger<CamaraToolExecutionService> logger)
    {
        _camaraApiClient = camaraApiClient;
        _logger = logger;
    }

    public async Task<JsonDocument> ExecuteAsync(
        string tool,
        IReadOnlyDictionary<string, string?> arguments,
        CancellationToken cancellationToken)
    {
        var (path, query) = BuildRequest(tool, arguments);

        _logger.LogInformation(
            "Executing Camara tool. Tool={Tool}, Path={Path}, Query={Query}",
            tool,
            path,
            JsonSerializer.Serialize(query));

        return await _camaraApiClient.GetJsonAsync(path, query, cancellationToken);
    }

    private static (string Path, IReadOnlyDictionary<string, string?> Query) BuildRequest(
        string tool,
        IReadOnlyDictionary<string, string?> arguments)
    {
        var normalizedTool = tool.Trim().ToLowerInvariant();
        return normalizedTool switch
        {
            "camara_api_get" => (SafeRelativePath(Required(arguments, "path")), RemoveArguments(arguments, "path")),

            "search_blocos" => ("blocos", WithoutEmptyValues(arguments)),
            "get_bloco" => ($"blocos/{Required(arguments, "idBloco")}", EmptyQuery()),

            "search_deputados" => ("deputados", WithoutEmptyValues(arguments)),
            "get_deputado" => ($"deputados/{Required(arguments, "idDeputado")}", EmptyQuery()),
            "get_deputado_despesas" => ($"deputados/{Required(arguments, "idDeputado")}/despesas", RemoveArguments(arguments, "idDeputado", "nome")),
            "get_deputado_discursos" => ($"deputados/{Required(arguments, "idDeputado")}/discursos", RemoveArguments(arguments, "idDeputado", "nome")),
            "get_deputado_eventos" => ($"deputados/{Required(arguments, "idDeputado")}/eventos", RemoveArguments(arguments, "idDeputado", "nome")),
            "get_deputado_frentes" => ($"deputados/{Required(arguments, "idDeputado")}/frentes", RemoveArguments(arguments, "idDeputado", "nome")),
            "get_deputado_orgaos" => ($"deputados/{Required(arguments, "idDeputado")}/orgaos", RemoveArguments(arguments, "idDeputado", "nome")),
            "get_deputado_profissoes" => ($"deputados/{Required(arguments, "idDeputado")}/profissoes", RemoveArguments(arguments, "idDeputado", "nome")),
            "get_deputado_votacoes" => ($"deputados/{Required(arguments, "idDeputado")}/votacoes", RemoveArguments(arguments, "idDeputado", "nome")),

            "search_eventos" => ("eventos", WithoutEmptyValues(arguments)),
            "get_evento" => ($"eventos/{Required(arguments, "idEvento")}", EmptyQuery()),
            "get_evento_orgaos" => ($"eventos/{Required(arguments, "idEvento")}/orgaos", EmptyQuery()),
            "get_evento_requerimentos" => ($"eventos/{Required(arguments, "idEvento")}/requerimentos", EmptyQuery()),
            "get_evento_votacoes" => ($"eventos/{Required(arguments, "idEvento")}/votacoes", EmptyQuery()),

            "search_frentes" => ("frentes", WithoutEmptyValues(arguments)),
            "get_frente" => ($"frentes/{Required(arguments, "idFrente")}", EmptyQuery()),
            "get_frente_membros" => ($"frentes/{Required(arguments, "idFrente")}/membros", RemoveArguments(arguments, "idFrente")),

            "search_grupos" => ("grupos", WithoutEmptyValues(arguments)),
            "get_grupo" => ($"grupos/{Required(arguments, "idGrupo")}", EmptyQuery()),
            "get_grupo_membros" => ($"grupos/{Required(arguments, "idGrupo")}/membros", RemoveArguments(arguments, "idGrupo")),

            "search_legislaturas" => ("legislaturas", WithoutEmptyValues(arguments)),
            "get_legislatura" => ($"legislaturas/{Required(arguments, "idLegislatura")}", EmptyQuery()),

            "search_orgaos" => ("orgaos", WithoutEmptyValues(arguments)),
            "get_orgao" => ($"orgaos/{Required(arguments, "idOrgao")}", EmptyQuery()),
            "get_orgao_eventos" => ($"orgaos/{Required(arguments, "idOrgao")}/eventos", RemoveArguments(arguments, "idOrgao")),
            "get_orgao_membros" => ($"orgaos/{Required(arguments, "idOrgao")}/membros", RemoveArguments(arguments, "idOrgao")),
            "get_orgao_votacoes" => ($"orgaos/{Required(arguments, "idOrgao")}/votacoes", RemoveArguments(arguments, "idOrgao")),

            "search_partidos" => ("partidos", WithoutEmptyValues(arguments)),
            "get_partido" => ($"partidos/{Required(arguments, "idPartido")}", EmptyQuery()),
            "get_partido_membros" => ($"partidos/{Required(arguments, "idPartido")}/membros", RemoveArguments(arguments, "idPartido")),

            "search_proposicoes" => ("proposicoes", WithoutEmptyValues(arguments)),
            "get_proposicao" => ($"proposicoes/{Required(arguments, "idProposicao")}", EmptyQuery()),
            "get_proposicao_autores" => ($"proposicoes/{Required(arguments, "idProposicao")}/autores", EmptyQuery()),
            "get_proposicao_relacionadas" => ($"proposicoes/{Required(arguments, "idProposicao")}/relacionadas", EmptyQuery()),
            "get_proposicao_temas" => ($"proposicoes/{Required(arguments, "idProposicao")}/temas", EmptyQuery()),
            "get_proposicao_tramitacoes" => ($"proposicoes/{Required(arguments, "idProposicao")}/tramitacoes", EmptyQuery()),
            "get_proposicao_votacoes" => ($"proposicoes/{Required(arguments, "idProposicao")}/votacoes", EmptyQuery()),

            "search_referencias" => ($"referencias/{SafeRelativePath(Required(arguments, "tipo"))}", RemoveArguments(arguments, "tipo")),

            "search_votacoes" => ("votacoes", WithoutEmptyValues(arguments)),
            "get_votacao" => ($"votacoes/{Required(arguments, "idVotacao")}", EmptyQuery()),
            "get_votacao_orientacoes" => ($"votacoes/{Required(arguments, "idVotacao")}/orientacoes", EmptyQuery()),
            "get_votacao_votos" => ($"votacoes/{Required(arguments, "idVotacao")}/votos", EmptyQuery()),

            _ => throw new InvalidOperationException($"Ferramenta não suportada pelo executor genérico: {tool}")
        };
    }

    private static string Required(IReadOnlyDictionary<string, string?> arguments, string key)
    {
        return arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"O argumento obrigatório '{key}' não foi informado.");
    }

    private static string SafeRelativePath(string path)
    {
        var value = path.Trim().TrimStart('/');
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains("://", StringComparison.Ordinal)
            || !Regex.IsMatch(value, "^[A-Za-z0-9_./-]+$"))
        {
            throw new InvalidOperationException($"Caminho de API inválido: '{path}'. Use apenas caminhos relativos da API v2.");
        }

        return value;
    }

    private static IReadOnlyDictionary<string, string?> RemoveArguments(IReadOnlyDictionary<string, string?> arguments, params string[] keys)
    {
        var keySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        return arguments
            .Where(item => !keySet.Contains(item.Key))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> WithoutEmptyValues(IReadOnlyDictionary<string, string?> arguments)
    {
        return arguments
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string?> EmptyQuery() => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}
