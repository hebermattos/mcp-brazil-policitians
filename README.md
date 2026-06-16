# mcp-brazil-policitians

MCP server em C# para consultar políticos brasileiros e dados legislativos usando a API de Dados Abertos da Câmara dos Deputados.

Fonte principal:

```text
https://dadosabertos.camara.leg.br/api/v2/
```

Documentação Swagger:

```text
https://dadosabertos.camara.leg.br/swagger/api.html
```

## Stack

- C# / .NET 8
- ASP.NET Core Minimal API
- MCP C# SDK (`ModelContextProtocol`)
- Transporte MCP via Streamable HTTP
- API REST da Câmara dos Deputados
- Cache local SQLite para respostas da API da Câmara

## Endpoints locais

| Endpoint | Descrição |
| --- | --- |
| `/` | Página HTML simples para testar o MCP pelo navegador. |
| `/mcp` | Endpoint MCP via Streamable HTTP. |
| `/health` | Health check simples do servidor. |

## Ferramentas MCP expostas

### Deputados

- `SearchDeputadosAsync`: busca deputados por nome, UF, partido e legislatura.
- `GetDeputadoAsync`: detalhe de um deputado por `idDeputado`.
- `GetDeputadoDespesasAsync`: despesas parlamentares por deputado, ano e mês.
- `GetDeputadoEventosAsync`: eventos relacionados a um deputado.
- `GetDeputadoHistoricoPartidosAsync`: histórico partidário do deputado.

### Proposições

- `SearchProposicoesAsync`: busca proposições por tipo, número, ano, ementa, autor e período.
- `GetProposicaoAsync`: detalhe de uma proposição.
- `GetProposicaoAutoresAsync`: autores de uma proposição.
- `GetProposicaoTramitacoesAsync`: tramitações de uma proposição.
- `GetProposicoesRelacionadasAsync`: proposições relacionadas.

### Eventos e órgãos

- `SearchEventosAsync`: busca eventos da Câmara.
- `GetEventoAsync`: detalhe de evento.
- `SearchOrgaosAsync`: busca órgãos/comissões.
- `GetOrgaoAsync`: detalhe de órgão/comissão.
- `GetOrgaoMembrosAsync`: membros de órgão/comissão.

### Raw/extensível

- `CamaraApiGetAsync`: chama qualquer caminho relativo da API v2 com query string opcional em JSON.

Exemplo:

```json
{
  "path": "deputados",
  "queryJson": "{\"nome\":\"Maria\",\"siglaUf\":\"RS\",\"itens\":5}"
}
```

## Como rodar

```bash
dotnet restore
dotnet run --project McpBrazilPoliticians.csproj
```

Por padrão, o ASP.NET Core sobe nos endereços configurados pelo ambiente. Para fixar uma porta local:

```bash
dotnet run --project McpBrazilPoliticians.csproj --urls http://localhost:5000
```

Depois acesse:

```text
http://localhost:5000/
```

O endpoint MCP fica em:

```text
http://localhost:5000/mcp
```

## Usando a página HTML

A página inicial permite:

1. Inicializar a sessão MCP.
2. Listar ferramentas disponíveis com `tools/list`.
3. Selecionar uma ferramenta.
4. Enviar argumentos JSON.
5. Executar a ferramenta com `tools/call`.

O campo de endpoint vem preenchido com `/mcp`, que funciona quando a página é servida pelo próprio servidor.

## Configuração em clientes MCP HTTP

Use o endpoint HTTP do servidor:

```text
http://localhost:5000/mcp
```

O transporte é Streamable HTTP. Clientes MCP compatíveis devem chamar esse endpoint usando JSON-RPC MCP sobre HTTP.

## Configuração opcional

O cliente da Câmara pode ser configurado por variáveis de ambiente:

```bash
CAMARA_API_BASE_URL=https://dadosabertos.camara.leg.br/api/v2/
CAMARA_API_TIMEOUT_SECONDS=30
CAMARA_API_CACHE_SQLITE_PATH=/caminho/para/camara-api-cache.sqlite
```

Quando `CAMARA_API_CACHE_SQLITE_PATH` não é definido, o cache é salvo em:

```text
<diretorio-da-aplicacao>/cache/camara-api-cache.sqlite
```

## Cache da API da Câmara

Todas as chamadas HTTP GET feitas pelo `CamaraApiClient` usam cache local em SQLite com validade de 1 hora.

Fluxo:

1. Monta a URL relativa da API da Câmara.
2. Procura uma resposta válida na tabela `ApiResponseCache`.
3. Se existir e ainda não expirou, retorna o JSON salvo.
4. Se não existir, chama a API da Câmara.
5. Se a resposta HTTP for 2xx, formata o JSON e salva no SQLite por 1 hora.
6. Respostas de erro não são cacheadas.

## Exemplos de prompts em um cliente MCP

- "Busque deputados do RS do PT."
- "Mostre detalhes do deputado com id 220593."
- "Liste despesas de um deputado em 2026."
- "Procure PLs de 2026 com ementa sobre inteligência artificial."
- "Liste eventos da Câmara entre 2026-06-01 e 2026-06-16."

## Segurança e limites

- O fallback `CamaraApiGetAsync` aceita apenas caminhos relativos.
- URLs absolutas são bloqueadas para evitar SSRF.
- `..` no path é bloqueado para evitar path traversal.
- Recomenda-se limitar `itens` em chamadas feitas por LLMs para evitar respostas muito grandes.
- A política de CORS atual é permissiva para facilitar testes locais. Restrinja as origens antes de expor em rede pública.
