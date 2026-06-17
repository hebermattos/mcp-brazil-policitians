using System.Text;
using System.Text.Json;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class ChatResponseFormatterService
{
    private readonly ILogger<ChatResponseFormatterService> _logger;

    public ChatResponseFormatterService(ILogger<ChatResponseFormatterService> logger)
    {
        _logger = logger;
    }

    public ChatPromptResponse Format(ChatPromptResponse response)
    {
        if (response.Data is null || string.IsNullOrWhiteSpace(response.Tool))
        {
            return response with { Answer = NormalizeAnswerText(response.Answer) };
        }

        var formattedAnswer = TryFormatFromCamaraData(response.Tool, response.Arguments, response.Data.Value);
        if (string.IsNullOrWhiteSpace(formattedAnswer))
        {
            formattedAnswer = NormalizeAnswerText(response.Answer);
        }

        if (!string.Equals(formattedAnswer, response.Answer, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Chat answer formatted as plain text. Tool={Tool}, OriginalLength={OriginalLength}, FormattedLength={FormattedLength}",
                response.Tool,
                response.Answer?.Length ?? 0,
                formattedAnswer.Length);
        }

        return response with { Answer = formattedAnswer };
    }

    private static string? TryFormatFromCamaraData(
        string tool,
        IReadOnlyDictionary<string, object?>? arguments,
        JsonElement data)
    {
        if (!data.TryGetProperty("dados", out var dados))
        {
            return null;
        }

        return tool.ToLowerInvariant() switch
        {
            "search_deputados" => FormatDeputados(dados, arguments),
            "search_proposicoes" => FormatProposicoes(dados, arguments),
            "search_eventos" => FormatGenericList("Eventos encontrados", dados),
            "search_orgaos" => FormatGenericList("Órgãos encontrados", dados),
            _ => FormatGenericData(dados)
        };
    }

    private static string FormatDeputados(JsonElement dados, IReadOnlyDictionary<string, object?>? arguments)
    {
        if (dados.ValueKind != JsonValueKind.Array || dados.GetArrayLength() == 0)
        {
            return "Não encontrei deputados com os filtros usados.";
        }

        var items = dados
            .EnumerateArray()
            .Select(item => new
            {
                Nome = GetString(item, "nome"),
                Partido = GetString(item, "siglaPartido"),
                Uf = GetString(item, "siglaUf"),
                Id = GetString(item, "id")
            })
            .OrderBy(item => item.Nome, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var titleParts = new List<string>();
        if (TryGetArgument(arguments, "siglaUf", out var uf)) titleParts.Add($"UF {uf}");
        if (TryGetArgument(arguments, "siglaPartido", out var partido)) titleParts.Add($"partido {partido}");

        var builder = new StringBuilder();
        builder.Append(titleParts.Count > 0
            ? $"Deputados encontrados — {string.Join(", ", titleParts)}"
            : "Deputados encontrados");
        builder.AppendLine();
        builder.AppendLine();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            builder.Append(i + 1).Append(". ").Append(item.Nome);

            if (!string.IsNullOrWhiteSpace(item.Partido) || !string.IsNullOrWhiteSpace(item.Uf))
            {
                builder.Append(" — ").Append(item.Partido);
                if (!string.IsNullOrWhiteSpace(item.Uf)) builder.Append('/').Append(item.Uf);
            }

            if (!string.IsNullOrWhiteSpace(item.Id)) builder.Append(" — ID ").Append(item.Id);
            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string FormatProposicoes(JsonElement dados, IReadOnlyDictionary<string, object?>? arguments)
    {
        if (dados.ValueKind != JsonValueKind.Array || dados.GetArrayLength() == 0)
        {
            return "Não encontrei proposições com os filtros usados.";
        }

        var items = dados
            .EnumerateArray()
            .Select(item => new
            {
                Id = GetString(item, "id"),
                Tipo = GetString(item, "siglaTipo"),
                Numero = GetString(item, "numero"),
                Ano = GetString(item, "ano"),
                Ementa = GetString(item, "ementa"),
                Data = GetString(item, "dataApresentacao")
            })
            .OrderByDescending(item => item.Ano)
            .ThenBy(item => item.Tipo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => TryParseInt(item.Numero), Comparer<int?>.Create((a, b) => Nullable.Compare(a, b)))
            .ToList();

        var builder = new StringBuilder();
        builder.Append("Proposições encontradas");
        if (TryGetArgument(arguments, "keywords", out var keywords)) builder.Append(" — ").Append(keywords);
        builder.AppendLine();
        builder.AppendLine();

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            builder.Append(i + 1).Append(". ");

            var label = string.Join(" ", new[] { item.Tipo, item.Numero, item.Ano }.Where(x => !string.IsNullOrWhiteSpace(x)));
            builder.Append(string.IsNullOrWhiteSpace(label) ? $"ID {item.Id}" : label);

            if (!string.IsNullOrWhiteSpace(item.Id)) builder.Append(" — ID ").Append(item.Id);
            if (!string.IsNullOrWhiteSpace(item.Data)) builder.Append(" — ").Append(FormatDate(item.Data));
            if (!string.IsNullOrWhiteSpace(item.Ementa)) builder.AppendLine().Append("   ").Append(SingleLine(item.Ementa));
            builder.AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string? FormatGenericData(JsonElement dados)
    {
        return dados.ValueKind switch
        {
            JsonValueKind.Array => FormatGenericList("Resultado", dados),
            JsonValueKind.Object => FormatGenericObject("Resultado", dados),
            _ => null
        };
    }

    private static string FormatGenericList(string title, JsonElement dados)
    {
        if (dados.ValueKind != JsonValueKind.Array || dados.GetArrayLength() == 0)
        {
            return "Nenhum resultado encontrado com os filtros usados.";
        }

        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine();

        var index = 1;
        foreach (var item in dados.EnumerateArray())
        {
            builder.Append(index++).Append(". ").Append(FormatGenericItem(item)).AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string FormatGenericObject(string title, JsonElement item)
    {
        var builder = new StringBuilder();
        builder.AppendLine(title);
        builder.AppendLine();

        foreach (var property in item.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                continue;
            }

            builder.Append("- ").Append(property.Name).Append(": ").Append(JsonValueToText(property.Value)).AppendLine();
        }

        return builder.ToString().Trim();
    }

    private static string FormatGenericItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return JsonValueToText(item);
        }

        var preferredLabel = GetString(item, "nome")
            ?? GetString(item, "titulo")
            ?? GetString(item, "descricao")
            ?? GetString(item, "sigla")
            ?? GetString(item, "id");

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredLabel)) parts.Add(preferredLabel);

        foreach (var property in item.EnumerateObject())
        {
            if (parts.Count >= 4) break;
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
            if (string.Equals(property.Value.ToString(), preferredLabel, StringComparison.OrdinalIgnoreCase)) continue;

            parts.Add($"{property.Name}: {JsonValueToText(property.Value)}");
        }

        return parts.Count == 0 ? item.GetRawText() : string.Join(" — ", parts);
    }

    private static string NormalizeAnswerText(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return "Sem resposta.";
        }

        var text = StripMarkdownJsonFence(answer.Trim());
        if (TryParseJson(text, out var json))
        {
            using (json)
            {
                var formatted = FormatGenericData(json.RootElement);
                if (!string.IsNullOrWhiteSpace(formatted)) return formatted;
            }
        }

        return text;
    }

    private static bool TryParseJson(string value, out JsonDocument json)
    {
        try
        {
            json = JsonDocument.Parse(value);
            return true;
        }
        catch
        {
            json = null!;
            return false;
        }
    }

    private static string StripMarkdownJsonFence(string value)
    {
        var text = value.Trim();
        text = RegexReplaceStart(text, "```json");
        text = RegexReplaceStart(text, "```");
        if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
        return text.Trim();
    }

    private static string RegexReplaceStart(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].TrimStart()
            : value;
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string JsonValueToText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "sim",
            JsonValueKind.False => "não",
            JsonValueKind.Null => string.Empty,
            _ => value.GetRawText()
        };
    }

    private static bool TryGetArgument(IReadOnlyDictionary<string, object?>? arguments, string key, out string value)
    {
        value = string.Empty;
        if (arguments is null || !arguments.TryGetValue(key, out var rawValue) || rawValue is null)
        {
            return false;
        }

        value = rawValue.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string SingleLine(string value)
    {
        return string.Join(" ", value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string FormatDate(string value)
    {
        return DateTimeOffset.TryParse(value, out var date)
            ? date.ToString("dd/MM/yyyy")
            : value;
    }

    private static int? TryParseInt(string? value)
    {
        return int.TryParse(value, out var number) ? number : null;
    }
}
