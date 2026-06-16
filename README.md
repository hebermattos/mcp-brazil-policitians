# mcp-brazil-policitians

MCP server em C# para consultar políticos brasileiros e dados legislativos usando a API de Dados Abertos da Câmara dos Deputados.

Fonte principal:

```text
https://dadosabertos.camara.leg.br/api/v2/
```

## Stack

- C# / .NET 8
- MCP C# SDK (`ModelContextProtocol`)
- Transporte MCP via `stdio`
- API REST da Câmara dos Deputados

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

Como o servidor usa transporte `stdio`, ele aguarda mensagens JSON-RPC MCP no stdin/stdout. Os logs são enviados para stderr para não quebrar o protocolo.

## Configuração no VS Code

O arquivo `.vscode/mcp.json` registra o servidor MCP:

```json
{
  "servers": {
    "brazil-politicians": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "${workspaceFolder}/McpBrazilPoliticians.csproj"]
    }
  }
}
```

## Configuração opcional

O cliente da Câmara pode ser configurado por variáveis de ambiente:

```bash
CAMARA_API_BASE_URL=https://dadosabertos.camara.leg.br/api/v2/
CAMARA_API_TIMEOUT_SECONDS=30
```

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
