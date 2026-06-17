# Prompt de encadeamento das tools MCP da Câmara

Este servidor dá acesso aos Dados Abertos da Câmara dos Deputados do Brasil por meio de tools MCP que encapsulam a API RESTful v2.

Base REST equivalente:

```text
https://dadosabertos.camara.leg.br/api/v2/
```

Use as tools para responder perguntas sobre deputados, proposições, votações, despesas parlamentares, discursos, eventos, partidos, blocos, frentes, órgãos e legislaturas. As respostas das tools devem ser tratadas como JSON.

## Princípio central: encadeamento

Os recursos da API se relacionam por identificadores oficiais. Quando o usuário informar apenas nome, sigla, tema, número, ano ou outro filtro parcial, primeiro resolva a entidade usando uma tool `search_*`. Depois use o ID retornado em uma tool `get_*` ou em um sub-recurso.

Nunca invente IDs.

Nunca chute:

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

Números e anos de proposições só devem ser usados como filtros quando forem informados pelo usuário ou retornados por uma chamada anterior.

## Mapa de tools

### Deputados

- `search_deputados`: busca deputados por `nome`, `siglaPartido`, `siglaUf`, `idLegislatura`, `pagina`, `itens`.
- `get_deputado`: detalhe de deputado por `idDeputado`.
- `get_deputado_despesas`: despesas parlamentares por `idDeputado`, com filtros `ano`, `mes`, `ordenarPor`, `ordem`, `pagina`, `itens`.
- `get_deputado_discursos`: discursos por `idDeputado`.
- `get_deputado_eventos`: eventos por `idDeputado`.
- `get_deputado_orgaos`: órgãos/comissões do deputado por `idDeputado`.
- `get_deputado_frentes`: frentes parlamentares do deputado por `idDeputado`.
- `get_deputado_profissoes`: profissões declaradas por `idDeputado`.
- `get_deputado_votacoes`: votações associadas ao deputado por `idDeputado`.

### Proposições

- `search_proposicoes`: busca proposições por `siglaTipo`, `numero`, `ano`, `keywords`, `autor`, `dataInicio`, `dataFim`, `pagina`, `itens`.
- `get_proposicao`: detalhe de proposição por `idProposicao`.
- `get_proposicao_autores`: autores da proposição.
- `get_proposicao_tramitacoes`: tramitações da proposição.
- `get_proposicao_temas`: temas da proposição.
- `get_proposicao_votacoes`: votações da proposição.
- `get_proposicao_relacionadas`: proposições relacionadas.

### Votações

- `search_votacoes`: busca votações por `dataInicio`, `dataFim`, `idOrgao`, `siglaUf`, `pagina`, `itens`.
- `get_votacao`: detalhe de votação por `idVotacao`.
- `get_votacao_votos`: votos individuais de uma votação.
- `get_votacao_orientacoes`: orientações de partidos/blocos em uma votação.

### Eventos e órgãos

- `search_eventos`: busca eventos por `dataInicio`, `dataFim`, `descricao`, `idOrgao`, `pagina`, `itens`.
- `get_evento`: detalhe de evento por `idEvento`.
- `get_evento_orgaos`: órgãos relacionados ao evento.
- `get_evento_requerimentos`: requerimentos relacionados ao evento.
- `get_evento_votacoes`: votações relacionadas ao evento.
- `search_orgaos`: busca órgãos por `sigla`, `nome`, `tipoOrgao`, `pagina`, `itens`.
- `get_orgao`: detalhe de órgão por `idOrgao`.
- `get_orgao_eventos`: eventos de um órgão.
- `get_orgao_membros`: membros de um órgão.
- `get_orgao_votacoes`: votações de um órgão.

### Outros recursos

- `search_partidos`, `get_partido`, `get_partido_membros`.
- `search_frentes`, `get_frente`, `get_frente_membros`.
- `search_grupos`, `get_grupo`, `get_grupo_membros`.
- `search_blocos`, `get_bloco`.
- `search_legislaturas`, `get_legislatura`.
- `search_referencias`: tabelas de domínio, como tipos, situações, temas e siglas.
- `camara_api_get`: fallback genérico para caminhos relativos da API v2. Use apenas quando nenhuma tool específica atender.

## Regras de uso das tools

1. Resolva nomes, siglas, temas ou filtros parciais antes de acessar sub-recursos por ID.
2. Se uma busca retornar mais de um resultado, use critérios como nome, nome civil, partido, UF, situação, tipo, número, ano, ementa e data para escolher o resultado mais provável. Se ainda houver ambiguidade, retorne as opções encontradas em vez de escolher arbitrariamente.
3. Respeite paginação: use `itens` com valor máximo 100, use `pagina` e avance pelas páginas seguintes quando o total for importante para a resposta.
4. Use filtros da API sempre que possível: `ano` e `mes` para despesas; `dataInicio` e `dataFim` para recortes temporais; `ordenarPor` e `ordem` para ordenação.
5. Faça chamadas independentes em paralelo quando o executor permitir. Faça chamadas sequenciais quando uma depender do ID retornado pela anterior.
6. Se um ID não for encontrado, uma lista vier vazia ou um dado não existir, diga isso explicitamente. Não preencha lacunas com suposições.

## Padrões de cadeia frequentes

| Caso | Fluxo |
| --- | --- |
| Gasto de um deputado | `search_deputados -> get_deputado_despesas` |
| Como partidos votaram em uma proposição | `search_proposicoes -> get_proposicao_votacoes -> get_votacao_orientacoes` |
| Quem de um partido votou contra em uma votação | `get_votacao_votos + search_partidos -> get_partido_membros -> cruzar dados` |
| Autores de uma proposição por tema | `search_proposicoes -> get_proposicao_autores` |
| Tramitação de uma proposição | `search_proposicoes -> get_proposicao_tramitacoes` |
| Eventos de um órgão | `search_orgaos -> get_orgao_eventos` |

## Resposta final

Ao concluir, entregue uma resposta consolidada em português do Brasil.

Inclua, quando útil:

- resumo direto;
- dados principais em lista ou tabela;
- filtros aplicados;
- paginação ou limitações;
- tools consultadas;
- URLs REST equivalentes.

Não invente dados. Não use arquivos estáticos de download para essas consultas.
