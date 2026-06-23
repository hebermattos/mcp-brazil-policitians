# Encadeamento com MCP Sampling

Este documento descreve o fluxo da tool `consultar_deputado_com_sampling`.

## Objetivo

Permitir que o servidor MCP faça uma consulta encadeada usando `sampling/createMessage` durante a execução da própria tool.

A tool não inventa identificadores oficiais da Câmara. Ela usa sampling para planejar, consulta a API da Câmara, usa sampling novamente para escolher o candidato correto e só então executa a chamada específica.

## Tool

```text
consultar_deputado_com_sampling
```

Parâmetros principais:

| Parâmetro | Descrição |
| --- | --- |
| `pergunta` | Pergunta em linguagem natural sobre deputado federal. |
| `pagina` | Página da chamada final. Padrão: `1`. |
| `itens` | Itens por página da chamada final. Padrão: `10`. |

## Fluxo interno

```text
pergunta do usuário
  -> sampling: extrai nome, intenção, ano, mês e datas
  -> search_deputados: busca candidatos na API da Câmara
  -> sampling: escolhe idDeputado correto entre os candidatos
  -> get_deputado ou get_deputado_*: consulta o recurso final
  -> sampling: gera resposta final em português usando apenas o JSON retornado
```

## Ações suportadas

| Intenção detectada | Chamada final |
| --- | --- |
| dados cadastrais, perfil | `get_deputado` |
| despesas, gastos, cota, reembolsos | `get_deputado_despesas` |
| discursos, pronunciamentos | `get_deputado_discursos` |
| agenda, eventos, reuniões | `get_deputado_eventos` |
| frentes parlamentares | `get_deputado_frentes` |
| órgãos, comissões, colegiados | `get_deputado_orgaos` |
| profissões declaradas | `get_deputado_profissoes` |
| votações do deputado | `get_deputado_votacoes` |
| histórico partidário | `get_deputado_historico_partidos` |

## Exemplo de uso

Pergunta:

```text
Mostre as despesas da Erika Hilton em 2026.
```

Fluxo esperado:

```text
sampling plan
  -> search_deputados(nome = "Erika Hilton")
  -> sampling selection(idDeputado)
  -> get_deputado_despesas(idDeputado, ano = 2026)
  -> sampling answer
```

## Requisitos

O cliente MCP conectado precisa suportar `sampling/createMessage`.

Se o cliente não suportar sampling, a tool retorna erro controlado informando que é necessário usar um cliente MCP com suporte a sampling.

## Observações de arquitetura

- A tool fica em `Tools/SamplingChainedQueryTools.cs`.
- A implementação usa `CamaraApiClient.GetJsonAsync(...)` para reaproveitar o cliente HTTP e o cache SQLite existentes.
- O servidor continua expondo as tools simples `search_*` e `get_*`; a nova tool é apenas uma orquestradora de alto nível.
- O retorno é JSON estruturado com:
  - `pergunta`
  - `samplingUsed`
  - `status`
  - `plan`
  - `selection`
  - `executedSteps`
  - `answer`
  - `data`
