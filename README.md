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
| `/mcp` | Página técnica de diagnóstico MCP, quando usada como `Mcp:PageEndpoint`. |
| `/mcp-server` | Endpoint MCP real quando há conflito entre página e transporte MCP. |
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
      "Model": "llama3.2:1B",
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
ollama pull llama3.2:1B
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
OLLAMA_MODEL=llama3.2:1B
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

## Encadeamento de ferramentas

As ferramentas seguem o padrão `search_* -> get_*`. Quando o usuário informar apenas nome, sigla, tema ou outro dado parcial, o sistema deve primeiro chamar uma ferramenta de busca para obter o identificador oficial da Câmara. Depois deve chamar a ferramenta específica com esse ID.

Regras principais:

- Nunca inventar `idDeputado`, `idProposicao`, `idVotacao`, `idOrgao`, `idEvento`, `idPartido`, `idFrente`, `idGrupo`, `idBloco` ou `idLegislatura`.
- Se o usuário informar apenas nome de deputado, chamar `search_deputados` antes da ferramenta específica.
- Se o usuário informar apenas tema, ementa, tipo, número ou autor de proposição, chamar `search_proposicoes` antes da ferramenta específica.
- Se houver múltiplos resultados possíveis, usar nome, partido, UF, situação, tipo, número, ano e ementa para reduzir ambiguidade.
- Se a ambiguidade continuar, retornar as opções encontradas em vez de escolher um ID sem evidência.
- Usar `pagina` e `itens` em listagens para evitar respostas grandes demais.
- Tratar lista vazia como ausência de dados encontrados para os filtros usados, não como erro.

Fluxos recomendados:

| Pergunta do usuário | Fluxo esperado |
| --- | --- |
| Dados cadastrais de deputado por nome | `search_deputados -> get_deputado` |
| Despesas, gastos, cota ou reembolsos por nome | `search_deputados -> get_deputado_despesas` |
| Discursos de deputado por nome | `search_deputados -> get_deputado_discursos` |
| Agenda, eventos ou reuniões de deputado por nome | `search_deputados -> get_deputado_eventos` |
| Frentes de deputado por nome | `search_deputados -> get_deputado_frentes` |
| Órgãos ou comissões de deputado por nome | `search_deputados -> get_deputado_orgaos` |
| Votações de deputado por nome | `search_deputados -> get_deputado_votacoes` |
| Detalhes de proposição por tema ou ementa | `search_proposicoes -> get_proposicao` |
| Autores de proposição sem ID | `search_proposicoes -> get_proposicao_autores` |
| Tramitação de proposição sem ID | `search_proposicoes -> get_proposicao_tramitacoes` |
| Votações de proposição sem ID | `search_proposicoes -> get_proposicao_votacoes` |
| Votos de uma votação sem ID | `search_votacoes -> get_votacao_votos` |
| Membros de órgão por sigla ou nome | `search_orgaos -> get_orgao_membros` |
| Membros de partido por sigla | `search_partidos -> get_partido_membros` |

Exemplo de plano encadeado usado internamente:

```json
{
  "steps": [
    {
      "tool": "search_deputados",
      "arguments": {
        "nome": "Erika Hilton",
        "pagina": "1",
        "itens": "1"
      },
      "saveAs": "deputado"
    },
    {
      "tool": "get_deputado_despesas",
      "arguments": {
        "idDeputado": "{{deputado.dados[0].id}}",
        "pagina": "1",
        "itens": "10"
      },
      "saveAs": "despesas"
    }
  ],
  "finalResult": "despesas"
}
```

## Ferramentas MCP expostas

### Deputados

- `search_deputados`: busca deputados por nome, UF, partido e legislatura. Use como primeira etapa para obter `idDeputado`.
- `get_deputado`: detalhe de um deputado por `idDeputado`.
- `get_deputado_despesas`: despesas parlamentares por deputado, ano e mês.
- `get_deputado_eventos`: eventos relacionados a um deputado.
- `get_deputado_discursos`: discursos de um deputado.
- `get_deputado_frentes`: frentes parlamentares de um deputado.
- `get_deputado_orgaos`: órgãos ou comissões de um deputado.
- `get_deputado_profissoes`: profissões declaradas de um deputado.
- `get_deputado_votacoes`: votações associadas a um deputado.

### Proposições

- `search_proposicoes`: busca proposições por tipo, número, ano, ementa, autor e período. Use como primeira etapa para obter `idProposicao`.
- `get_proposicao`: detalhe de uma proposição.
- `get_proposicao_autores`: autores de uma proposição.
- `get_proposicao_tramitacoes`: tramitações de uma proposição.
- `get_proposicao_temas`: temas de uma proposição.
- `get_proposicao_votacoes`: votações de uma proposição.
- `get_proposicao_relacionadas`: proposições relacionadas.

### Eventos e órgãos

- `search_eventos`: busca eventos da Câmara. Use como primeira etapa para obter `idEvento`.
- `get_evento`: detalhe de evento.
- `get_evento_orgaos`: órgãos relacionados a evento.
- `get_evento_requerimentos`: requerimentos relacionados a evento.
- `get_evento_votacoes`: votações relacionadas a evento.
- `search_orgaos`: busca órgãos por sigla, nome e tipo. Use como primeira etapa para obter `idOrgao`.
- `get_orgao`: detalhe de órgão.
- `get_orgao_eventos`: eventos de órgão.
- `get_orgao_membros`: membros de órgão.
- `get_orgao_votacoes`: votações de órgão.

### Votações, partidos e grupos

- `search_votacoes`: busca votações. Use como primeira etapa para obter `idVotacao`.
- `get_votacao`: detalhe de votação.
- `get_votacao_orientacoes`: orientações de bancada de uma votação.
- `get_votacao_votos`: votos individuais de uma votação.
- `search_partidos`: busca partidos. Use como primeira etapa para obter `idPartido`.
- `get_partido`: detalhe de partido.
- `get_partido_membros`: membros de partido.
- `search_frentes`: busca frentes parlamentares. Use como primeira etapa para obter `idFrente`.
- `get_frente`: detalhe de frente parlamentar.
- `get_frente_membros`: membros de frente parlamentar.
- `search_grupos`: busca grupos parlamentares. Use como primeira etapa para obter `idGrupo`.
- `get_grupo`: detalhe de grupo parlamentar.
- `get_grupo_membros`: membros de grupo parlamentar.
- `search_blocos`: busca blocos parlamentares. Use como primeira etapa para obter `idBloco`.
- `get_bloco`: detalhe de bloco parlamentar.
- `search_legislaturas`: busca legislaturas. Use como primeira etapa para obter `idLegislatura`.
- `get_legislatura`: detalhe de legislatura.
- `search_referencias`: consulta tabelas auxiliares da API.
