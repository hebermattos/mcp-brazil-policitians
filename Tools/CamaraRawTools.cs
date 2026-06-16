using System.ComponentModel;
using System.Text.Json;
using McpBrazilPoliticians.Services;
using ModelContextProtocol.Server;

namespace McpBrazilPoliticians.Tools;

[McpServerToolType]
public static class CamaraRawTools
{
    [McpServerTool, Description("Calls any relative Câmara Dados Abertos API v2 path with an optional JSON object of query string parameters. Use when a typed tool is not available.")]
    public static Task<string> CamaraApiGetAsync(
        [Description("Relative path under /api/v2. Examples: deputados, deputados/220593, proposicoes/2345631/autores.")] string path,
        [Description("Optional JSON object with query parameters. Example: {\"pagina\":1,\"itens\":10}.")] string? queryJson = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, object?>? query = null;

        if (!string.IsNullOrWhiteSpace(queryJson))
        {
            using var document = JsonDocument.Parse(queryJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("queryJson must be a JSON object.", nameof(queryJson));
            }

            query = [];
            foreach (var property in document.RootElement.EnumerateObject())
            {
                query[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number when property.Value.TryGetInt64(out var longValue) => longValue,
                    JsonValueKind.Number => property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => property.Value.GetRawText()
                };
            }
        }

        return CamaraApiClient.GetJsonAsync(path, query, cancellationToken);
    }
}
