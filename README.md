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
- MCP C# SDK (`ModelContextProtocol`)
- Transporte MCP via `stdio`
- API REST da Câmara dos Deputados
- Cache local SQLite para respostas da API da Câmara

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

Depois de salvar o arquivo, reinicie o servidor MCP pelo VS Code caso ele já esteja aberto.

## Abrir o chat do VS Code com suporte MCP

No VS Code, o suporte a MCP fica disponível pelo GitHub Copilot Chat em **Agent Mode**.

Passos:

1. Abra este repositório no VS Code.
2. Abra o chat:
   - `Ctrl + Alt + I`; ou
   - `Ctrl + Shift + P` e execute **Chat: Open Chat**.
3. No painel do chat, selecione o modo **Agent**.
4. Verifique se o servidor MCP foi carregado:
   - `Ctrl + Shift + P`;
   - execute **MCP: List Servers** ou **MCP: Show Installed Servers**.
5. Se o servidor `brazil-politicians` aparecer parado, execute **MCP: Start Server**.
6. No chat, peça para o agente usar as ferramentas MCP do workspace.

Exemplos de prompt:

```text
Use o MCP para consultar deputados pela API de dados abertos da Câmara.
```

```text
Use as ferramentas MCP deste workspace para listar deputados do Rio Grande do Sul.
```

```text
Use o MCP brazil-politicians para procurar proposições de 2026 sobre inteligência artificial.
```

Observações:

- O chat precisa estar em **Agent Mode** para conseguir usar ferramentas MCP.
- Se o servidor MCP não aparecer na lista, verifique o arquivo `.vscode/mcp.json` e reinicie o VS Code.
- Se o comando `dotnet run` falhar, teste o comando manualmente no terminal para validar o caminho do projeto e o SDK instalado.

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
