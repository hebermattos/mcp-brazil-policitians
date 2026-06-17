using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public static class McpDiagnosticToolCatalog
{
    public static IReadOnlyList<McpDiagnosticTool> All { get; } =
    [
        new("camara_api_get", "Executa GET genérico em qualquer caminho relativo da API v2 da Câmara. Use apenas quando nenhuma ferramenta específica atender à pergunta. Não use URL absoluta; informe somente o path relativo.", ["path", "pagina", "itens", "ordem", "ordenarPor"]),

        new("search_blocos", "Lista blocos parlamentares. Use para descobrir idBloco antes de chamar get_bloco quando o usuário informar apenas legislatura ou quiser listar blocos.", ["idLegislatura", "pagina", "itens"]),
        new("get_bloco", "Obtém detalhes de um bloco parlamentar pelo idBloco. Se o usuário não informou idBloco, chame search_blocos antes.", ["idBloco"]),

        new("search_deputados", "Busca deputados por nome, UF, partido, legislatura e paginação. Use primeiro quando o usuário informar nome ou dados parciais de deputado. A resposta traz id, que deve ser usado em get_deputado, get_deputado_despesas, get_deputado_discursos, get_deputado_eventos, get_deputado_frentes, get_deputado_orgaos, get_deputado_profissoes e get_deputado_votacoes.", ["nome", "siglaUf", "siglaPartido", "idLegislatura", "pagina", "itens"]),
        new("get_deputado", "Obtém detalhes de um deputado pelo idDeputado. Se o usuário informou apenas nome, partido ou UF, chame search_deputados antes e use dados[0].id somente quando a busca não for ambígua.", ["idDeputado"]),
        new("get_deputado_despesas", "Lista despesas parlamentares de um deputado pelo idDeputado. Use para perguntas sobre gastos, cota parlamentar, fornecedores, notas fiscais ou reembolsos. Se o usuário informou apenas o nome do deputado, encadeie search_deputados -> get_deputado_despesas.", ["idDeputado", "ano", "mes", "pagina", "itens"]),
        new("get_deputado_discursos", "Lista discursos de um deputado pelo idDeputado. Use para perguntas sobre pronunciamentos, falas ou discursos oficiais. Se o usuário informou apenas nome, encadeie search_deputados -> get_deputado_discursos.", ["idDeputado", "dataInicio", "dataFim", "pagina", "itens"]),
        new("get_deputado_eventos", "Lista eventos relacionados a um deputado pelo idDeputado. Use para agenda, reuniões, sessões, audiências e comissões. Se o usuário informou apenas nome, encadeie search_deputados -> get_deputado_eventos.", ["idDeputado", "dataInicio", "dataFim", "pagina", "itens"]),
        new("get_deputado_frentes", "Lista frentes parlamentares de um deputado pelo idDeputado. Se o usuário informou apenas nome, encadeie search_deputados -> get_deputado_frentes.", ["idDeputado", "pagina", "itens"]),
        new("get_deputado_orgaos", "Lista órgãos, comissões e colegiados dos quais um deputado participa pelo idDeputado. Se o usuário informou apenas nome, encadeie search_deputados -> get_deputado_orgaos.", ["idDeputado", "pagina", "itens"]),
        new("get_deputado_profissoes", "Lista profissões declaradas de um deputado pelo idDeputado. Se o usuário informou apenas nome, encadeie search_deputados -> get_deputado_profissoes.", ["idDeputado"]),
        new("get_deputado_votacoes", "Lista votações associadas a um deputado pelo idDeputado. Se o usuário informou apenas nome, encadeie search_deputados -> get_deputado_votacoes.", ["idDeputado", "pagina", "itens"]),

        new("search_eventos", "Busca eventos por data, descrição, órgão e paginação. Use para descobrir idEvento antes de chamar get_evento, get_evento_orgaos, get_evento_requerimentos ou get_evento_votacoes.", ["dataInicio", "dataFim", "descricao", "idOrgao", "pagina", "itens"]),
        new("get_evento", "Obtém detalhes de um evento pelo idEvento. Se o usuário não informou idEvento, chame search_eventos antes.", ["idEvento"]),
        new("get_evento_orgaos", "Lista órgãos relacionados a um evento pelo idEvento. Se o usuário não informou idEvento, encadeie search_eventos -> get_evento_orgaos.", ["idEvento"]),
        new("get_evento_requerimentos", "Lista requerimentos relacionados a um evento pelo idEvento. Se o usuário não informou idEvento, encadeie search_eventos -> get_evento_requerimentos.", ["idEvento"]),
        new("get_evento_votacoes", "Lista votações relacionadas a um evento pelo idEvento. Se o usuário não informou idEvento, encadeie search_eventos -> get_evento_votacoes.", ["idEvento"]),

        new("search_frentes", "Lista frentes parlamentares. Use para descobrir idFrente antes de chamar get_frente ou get_frente_membros.", ["idLegislatura", "pagina", "itens"]),
        new("get_frente", "Obtém detalhes de uma frente parlamentar pelo idFrente. Se o usuário não informou idFrente, chame search_frentes antes.", ["idFrente"]),
        new("get_frente_membros", "Lista membros de uma frente parlamentar pelo idFrente. Se o usuário não informou idFrente, encadeie search_frentes -> get_frente_membros.", ["idFrente", "pagina", "itens"]),

        new("search_grupos", "Lista grupos parlamentares. Use para descobrir idGrupo antes de chamar get_grupo ou get_grupo_membros.", ["idLegislatura", "pagina", "itens"]),
        new("get_grupo", "Obtém detalhes de um grupo parlamentar pelo idGrupo. Se o usuário não informou idGrupo, chame search_grupos antes.", ["idGrupo"]),
        new("get_grupo_membros", "Lista membros de um grupo parlamentar pelo idGrupo. Se o usuário não informou idGrupo, encadeie search_grupos -> get_grupo_membros.", ["idGrupo", "pagina", "itens"]),

        new("search_legislaturas", "Lista legislaturas da Câmara. Use para descobrir idLegislatura antes de filtrar deputados, partidos, blocos, frentes ou grupos por legislatura.", ["dataInicio", "dataFim", "pagina", "itens"]),
        new("get_legislatura", "Obtém detalhes de uma legislatura pelo idLegislatura. Se o usuário não informou idLegislatura, chame search_legislaturas antes.", ["idLegislatura"]),

        new("search_orgaos", "Busca órgãos por sigla, nome, tipo e paginação. Use para descobrir idOrgao antes de chamar get_orgao, get_orgao_eventos, get_orgao_membros ou get_orgao_votacoes.", ["sigla", "nome", "tipoOrgao", "pagina", "itens"]),
        new("get_orgao", "Obtém detalhes de um órgão pelo idOrgao. Se o usuário informou apenas sigla ou nome, chame search_orgaos antes.", ["idOrgao"]),
        new("get_orgao_eventos", "Lista eventos de um órgão pelo idOrgao. Se o usuário informou apenas sigla ou nome do órgão, encadeie search_orgaos -> get_orgao_eventos.", ["idOrgao", "dataInicio", "dataFim", "pagina", "itens"]),
        new("get_orgao_membros", "Lista membros de um órgão pelo idOrgao. Se o usuário informou apenas sigla ou nome do órgão, encadeie search_orgaos -> get_orgao_membros.", ["idOrgao", "idLegislatura", "pagina", "itens"]),
        new("get_orgao_votacoes", "Lista votações de um órgão pelo idOrgao. Se o usuário informou apenas sigla ou nome do órgão, encadeie search_orgaos -> get_orgao_votacoes.", ["idOrgao", "dataInicio", "dataFim", "pagina", "itens"]),

        new("search_partidos", "Lista partidos por sigla e paginação. Use para descobrir idPartido antes de chamar get_partido ou get_partido_membros.", ["sigla", "pagina", "itens"]),
        new("get_partido", "Obtém detalhes de um partido pelo idPartido. Se o usuário informou apenas sigla, chame search_partidos antes.", ["idPartido"]),
        new("get_partido_membros", "Lista membros de um partido pelo idPartido. Se o usuário informou apenas sigla do partido, encadeie search_partidos -> get_partido_membros.", ["idPartido", "idLegislatura", "pagina", "itens"]),

        new("search_proposicoes", "Busca proposições por tipo, número, ano, autor, keywords e paginação. Use primeiro quando o usuário informar tema, ementa, autor, tipo/número/ano ou dados parciais. A resposta traz id, que deve ser usado em get_proposicao, get_proposicao_autores, get_proposicao_relacionadas, get_proposicao_temas, get_proposicao_tramitacoes e get_proposicao_votacoes.", ["siglaTipo", "numero", "ano", "keywords", "autor", "dataInicio", "dataFim", "pagina", "itens"]),
        new("get_proposicao", "Obtém detalhes de uma proposição pelo idProposicao. Se o usuário informou apenas tema, ementa, tipo, número ou ano sem ID, chame search_proposicoes antes.", ["idProposicao"]),
        new("get_proposicao_autores", "Lista autores de uma proposição pelo idProposicao. Se o usuário não informou idProposicao, encadeie search_proposicoes -> get_proposicao_autores.", ["idProposicao"]),
        new("get_proposicao_relacionadas", "Lista proposições relacionadas a uma proposição pelo idProposicao. Se o usuário não informou idProposicao, encadeie search_proposicoes -> get_proposicao_relacionadas.", ["idProposicao"]),
        new("get_proposicao_temas", "Lista temas de uma proposição pelo idProposicao. Se o usuário não informou idProposicao, encadeie search_proposicoes -> get_proposicao_temas.", ["idProposicao"]),
        new("get_proposicao_tramitacoes", "Lista tramitações de uma proposição pelo idProposicao. Se o usuário não informou idProposicao, encadeie search_proposicoes -> get_proposicao_tramitacoes.", ["idProposicao"]),
        new("get_proposicao_votacoes", "Lista votações de uma proposição pelo idProposicao. Se o usuário não informou idProposicao, encadeie search_proposicoes -> get_proposicao_votacoes.", ["idProposicao"]),

        new("search_referencias", "Consulta tabelas de referência da API, usando tipo como caminho relativo após /referencias. Use para descobrir valores permitidos de tipos, situações, siglas e demais tabelas auxiliares.", ["tipo"]),

        new("search_votacoes", "Busca votações por data, órgão, UF e paginação. Use para descobrir idVotacao antes de chamar get_votacao, get_votacao_orientacoes ou get_votacao_votos.", ["dataInicio", "dataFim", "idOrgao", "siglaUf", "pagina", "itens"]),
        new("get_votacao", "Obtém detalhes de uma votação pelo idVotacao. Se o usuário não informou idVotacao, chame search_votacoes antes.", ["idVotacao"]),
        new("get_votacao_orientacoes", "Lista orientações de bancada de uma votação pelo idVotacao. Se o usuário não informou idVotacao, encadeie search_votacoes -> get_votacao_orientacoes.", ["idVotacao"]),
        new("get_votacao_votos", "Lista votos individuais de uma votação pelo idVotacao. Se o usuário não informou idVotacao, encadeie search_votacoes -> get_votacao_votos.", ["idVotacao"])
    ];
}
