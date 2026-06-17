using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public static class McpDiagnosticToolCatalog
{
    public static IReadOnlyList<McpDiagnosticTool> All { get; } =
    [
        new("camara_api_get", "Executa GET genérico em qualquer caminho relativo da API v2 da Câmara.", ["path", "pagina", "itens", "ordem", "ordenarPor"]),

        new("search_blocos", "Lista blocos parlamentares.", ["idLegislatura", "pagina", "itens"]),
        new("get_bloco", "Obtém detalhes de um bloco parlamentar.", ["idBloco"]),

        new("search_deputados", "Busca deputados por nome, UF, partido, legislatura e paginação.", ["nome", "siglaUf", "siglaPartido", "idLegislatura", "pagina", "itens"]),
        new("get_deputado", "Obtém detalhes de um deputado pelo ID.", ["idDeputado"]),
        new("get_deputado_despesas", "Lista despesas de um deputado pelo ID.", ["idDeputado", "ano", "mes", "pagina", "itens"]),
        new("get_deputado_discursos", "Lista discursos de um deputado.", ["idDeputado", "dataInicio", "dataFim", "pagina", "itens"]),
        new("get_deputado_eventos", "Lista eventos de um deputado.", ["idDeputado", "dataInicio", "dataFim", "pagina", "itens"]),
        new("get_deputado_frentes", "Lista frentes parlamentares de um deputado.", ["idDeputado", "pagina", "itens"]),
        new("get_deputado_orgaos", "Lista órgãos dos quais um deputado participa.", ["idDeputado", "pagina", "itens"]),
        new("get_deputado_profissoes", "Lista profissões declaradas de um deputado.", ["idDeputado"]),
        new("get_deputado_votacoes", "Lista votações de um deputado pelo ID.", ["idDeputado", "pagina", "itens"]),

        new("search_eventos", "Busca eventos por data, descrição, órgão e paginação.", ["dataInicio", "dataFim", "descricao", "idOrgao", "pagina", "itens"]),
        new("get_evento", "Obtém detalhes de um evento.", ["idEvento"]),
        new("get_evento_orgaos", "Lista órgãos relacionados a um evento.", ["idEvento"]),
        new("get_evento_requerimentos", "Lista requerimentos relacionados a um evento.", ["idEvento"]),
        new("get_evento_votacoes", "Lista votações relacionadas a um evento.", ["idEvento"]),

        new("search_frentes", "Lista frentes parlamentares.", ["idLegislatura", "pagina", "itens"]),
        new("get_frente", "Obtém detalhes de uma frente parlamentar.", ["idFrente"]),
        new("get_frente_membros", "Lista membros de uma frente parlamentar.", ["idFrente", "pagina", "itens"]),

        new("search_grupos", "Lista grupos parlamentares.", ["idLegislatura", "pagina", "itens"]),
        new("get_grupo", "Obtém detalhes de um grupo parlamentar.", ["idGrupo"]),
        new("get_grupo_membros", "Lista membros de um grupo parlamentar.", ["idGrupo", "pagina", "itens"]),

        new("search_legislaturas", "Lista legislaturas da Câmara.", ["dataInicio", "dataFim", "pagina", "itens"]),
        new("get_legislatura", "Obtém detalhes de uma legislatura.", ["idLegislatura"]),

        new("search_orgaos", "Busca órgãos por sigla, nome, tipo e paginação.", ["sigla", "nome", "tipoOrgao", "pagina", "itens"]),
        new("get_orgao", "Obtém detalhes de um órgão.", ["idOrgao"]),
        new("get_orgao_eventos", "Lista eventos de um órgão.", ["idOrgao", "dataInicio", "dataFim", "pagina", "itens"]),
        new("get_orgao_membros", "Lista membros de um órgão.", ["idOrgao", "idLegislatura", "pagina", "itens"]),
        new("get_orgao_votacoes", "Lista votações de um órgão.", ["idOrgao", "dataInicio", "dataFim", "pagina", "itens"]),

        new("search_partidos", "Lista partidos.", ["sigla", "pagina", "itens"]),
        new("get_partido", "Obtém detalhes de um partido.", ["idPartido"]),
        new("get_partido_membros", "Lista membros de um partido.", ["idPartido", "idLegislatura", "pagina", "itens"]),

        new("search_proposicoes", "Busca proposições por tipo, número, ano, autor, keywords e paginação.", ["siglaTipo", "numero", "ano", "keywords", "autor", "dataInicio", "dataFim", "pagina", "itens"]),
        new("get_proposicao", "Obtém detalhes de uma proposição pelo ID.", ["idProposicao"]),
        new("get_proposicao_autores", "Lista autores de uma proposição.", ["idProposicao"]),
        new("get_proposicao_relacionadas", "Lista proposições relacionadas.", ["idProposicao"]),
        new("get_proposicao_temas", "Lista temas de uma proposição.", ["idProposicao"]),
        new("get_proposicao_tramitacoes", "Lista tramitações de uma proposição.", ["idProposicao"]),
        new("get_proposicao_votacoes", "Lista votações de uma proposição.", ["idProposicao"]),

        new("search_referencias", "Consulta tabelas de referência da API, usando tipo como caminho relativo após /referencias.", ["tipo"]),

        new("search_votacoes", "Busca votações.", ["dataInicio", "dataFim", "idOrgao", "siglaUf", "pagina", "itens"]),
        new("get_votacao", "Obtém detalhes de uma votação.", ["idVotacao"]),
        new("get_votacao_orientacoes", "Lista orientações de bancada de uma votação.", ["idVotacao"]),
        new("get_votacao_votos", "Lista votos individuais de uma votação.", ["idVotacao"])
    ];
}
