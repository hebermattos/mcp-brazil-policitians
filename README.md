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
- Backend de chat com OpenAI ou Ollama local
- API REST da Câmara dos Deputados
- Cache local SQLite para respostas da API da Câmara

## Endpoints locais

| Endpoint | Descrição |
| --- | --- |
| `/` | Página HTML de chat para prompts em linguagem natural. |
| `/api/chat` | Backend HTTP usado pela página de chat. Configurável em `Chat:Endpoint`. |
| `/api/client-settings` | Configurações públicas usadas pelo frontend. |
| `/mcp` | Endpoint MCP via Streamable HTTP. Configurável em `Mcp:Endpoint`. |
| `/health` | Health check simples do servidor. |

## Configuração via appsettings

Todas as configurações principais podem ser definidas no `appsettings.json`. O projeto já vem com valores padrão:

```json
{
  "Mcp": {
    "Endpoint": "/mcp",
    "Stateless": true
  },
  "Chat": {
    "Endpoint": "/api/chat",
    "Provider": "ollama",
    "DefaultSearchItems": 10,
    "DefaultSearchPage": 1,
    "MaxDataJsonChars": 20000,
    "OpenAI": {
      "BaseUrl": "https://api.openai.com/v1/",
      "ApiKey": "",
      "Model": "gpt-4.1-mini",
      "TimeoutSeconds": 60
    },
    "Ollama": {
      "BaseUrl": "http://localhost:11434",
      "Model": "llama3.1:8b",
      "TimeoutSeconds": 120,
      "UseJsonFormat": true
    }
  },
  "CamaraApi": {
    "BaseUrl": "https://dadosabertos.camara.leg.br/api/v2/",
    "TimeoutSeconds": 30,
    "CacheSqlitePath": "",
    "CacheTtlMinutes": 60
  },
  "Cors": {
    "AllowAnyOrigin": true,
    "AllowedOrigins": [],
    "ExposedHeaders": [
      "Mcp-Session-Id"
    ]
  }
}
```

### Provedor padrão

O padrão é `ollama`:

```json
{
  "Chat": {
    "Provider": "ollama"
  }
}
```

Para usar OpenAI:

```json
{
  "Chat": {
    "Provider": "openai",
    "OpenAI": {
      "ApiKey": "sua-chave",
      "Model": "gpt-4.1-mini"
    }
  }
}
```

### Ollama local

Antes de rodar com o padrão atual:

```bash
ollama pull llama3.1:8b
ollama serve
```

Se quiser outro modelo local:

```json
{
  "Chat": {
    "Ollama": {
      "Model": "qwen2.5-coder:3b"
    }
  }
}
```

## Variáveis de ambiente opcionais

O `appsettings.json` é a forma principal de configuração. Algumas variáveis de ambiente continuam suportadas como fallback/override operacional:

```bash
MCP_ENDPOINT=/mcp
MCP_STATELESS=true
CHAT_ENDPOINT=/api/chat
CHAT_PROVIDER=ollama
CHAT_DEFAULT_SEARCH_ITEMS=10
CHAT_DEFAULT_SEARCH_PAGE=1
CHAT_MAX_DATA_JSON_CHARS=20000
OPENAI_BASE_URL=https://api.openai.com/v1/
OPENAI_API_KEY=sua-chave
OPENAI_MODEL=gpt-4.1-mini
OPENAI_TIMEOUT_SECONDS=60
OLLAMA_BASE_URL=http://localhost:11434
OLLAMA_MODEL=llama3.1:8b
OLLAMA_TIMEOUT_SECONDS=120
OLLAMA_USE_JSON_FORMAT=true
CAMARA_API_BASE_URL=https://dadosabertos.camara.leg.br/api/v2/
CAMARA_API_TIMEOUT_SECONDS=30
CORS_ALLOW_ANY_ORIGIN=true
```

## Como rodar

```bash
dotnet restore
dotnet run --project McpBrazilPoliticians.csproj --urls http://localhost:5000
```

Acesse:

```text
http://localhost:5000/
```

O endpoint MCP padrão fica em:

```text
http://localhost:5000/mcp
```

O backend de chat padrão fica em:

```text
http://localhost:5000/api/chat
```

## Usando a página de chat

A página inicial carrega `/api/client-settings` para descobrir o endpoint de chat configurado e depois envia o prompt para esse endpoint.

Fluxo do backend:

1. Recebe o prompt do usuário.
2. Usa o provedor configurado (`openai` ou `ollama`) para escolher a consulta adequada.
3. Consulta a API de Dados Abertos da Câmara.
4. Usa o provedor configurado novamente para transformar o JSON retornado em uma resposta em português.
5. Retorna a resposta, a ferramenta lógica usada, os argumentos e os dados brutos.

Exemplos de prompt:

- "Busque deputados do RS do PT."
- "Mostre detalhes do deputado com id 220593."
- "Procure proposições sobre escala 6x1."
- "Procure PLs de 2026 com ementa sobre inteligência artificial."
- "Liste eventos da Câmara entre 2026-06-01 e 2026-06-16."

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

## Configuração em clientes MCP HTTP

Use o endpoint HTTP configurado em `Mcp:Endpoint`:

```text
http://localhost:5000/mcp
```

O transporte é Streamable HTTP. Clientes MCP compatíveis devem chamar esse endpoint usando JSON-RPC MCP sobre HTTP.

## Segurança e limites

- O fallback `CamaraApiGetAsync` aceita apenas caminhos relativos.
- URLs absolutas são bloqueadas para evitar SSRF.
- `..` no path é bloqueado para evitar path traversal.
- Recomenda-se limitar `Chat:DefaultSearchItems` para evitar respostas muito grandes.
- A política de CORS atual é permissiva para facilitar testes locais. Restrinja as origens antes de expor em rede pública.
- Não exponha `Chat:OpenAI:ApiKey` no frontend. A chave fica somente no backend.
