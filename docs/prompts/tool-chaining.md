# MCP tool chaining prompt notes

Este documento descreve o comportamento esperado do planejador de ferramentas do projeto.

## Objetivo

O assistente deve responder perguntas usando ferramentas MCP que consultam a API JSON da Câmara dos Deputados.

Base REST equivalente:

https://dadosabertos.camara.leg.br/api/v2/

As chamadas devem ser tratadas como JSON, usando `Accept: application/json` ou `?formato=json` quando a URL REST equivalente for exibida.

## Regra principal

Use chamadas encadeadas quando uma pergunta depender de um identificador.

Fluxo padrão:

1. Usar uma ferramenta `search_*` para encontrar a entidade.
2. Extrair o identificador oficial retornado pela API.
3. Usar esse identificador em uma ferramenta `get_*` ou em um sub-recurso.
4. Consolidar os dados retornados.

Nunca invente IDs.

## IDs que não devem ser inventados

- `idDeputado`
- `idProposicao`
- `idVotacao`
- `idOrgao`
- `idEvento`
- `idPartido`
- `idFrente`
- `idGrupo`
- `idBloco`
- `idLegislatura`

## Ferramentas de descoberta

- `search_deputados`
- `search_proposicoes`
- `search_votacoes`
- `search_eventos`
- `search_orgaos`
- `search_partidos`
- `search_frentes`
- `search_grupos`
- `search_blocos`
- `search_legislaturas`
- `search_referencias`

## Ferramentas por identificador

- `get_deputado`
- `get_deputado_despesas`
- `get_deputado_discursos`
- `get_deputado_eventos`
- `get_deputado_orgaos`
- `get_deputado_frentes`
- `get_deputado_profissoes`
- `get_deputado_votacoes`
- `get_proposicao`
- `get_proposicao_autores`
- `get_proposicao_tramitacoes`
- `get_proposicao_temas`
- `get_proposicao_votacoes`
- `get_proposicao_relacionadas`
- `get_votacao`
- `get_votacao_votos`
- `get_votacao_orientacoes`
- `get_evento`
- `get_evento_orgaos`
- `get_evento_requerimentos`
- `get_evento_votacoes`
- `get_orgao`
- `get_orgao_eventos`
- `get_orgao_membros`
- `get_orgao_votacoes`
- `get_partido`
- `get_partido_membros`
- `get_frente`
- `get_frente_membros`
- `get_grupo`
- `get_grupo_membros`
- `get_bloco`
- `get_legislatura`

## Paginação

Use `itens` com valor máximo 100 e controle `pagina` quando a resposta puder ter muitos itens.

Quando a pergunta exigir totalização ou comparação, continue consultando páginas até obter dados suficientes ou informe que a resposta está limitada.

## Resposta final

A resposta final deve incluir:

1. Resumo direto.
2. Dados principais.
3. Filtros usados.
4. Ferramentas chamadas.
5. URLs REST equivalentes, quando útil.
