using System.Text.Json;
using System.Text.RegularExpressions;

namespace McpBrazilPoliticians.Services;

public sealed class CamaraJsonPathResolver
{
    private static readonly Regex SegmentRegex = new(
        @"^(?<name>[A-Za-z0-9_]+)(?:\[(?<index>\d+)\])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Resolve(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("O caminho JSON da referência está vazio.");
        }

        var current = root;
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = SegmentRegex.Match(rawSegment);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Segmento de caminho JSON inválido: '{rawSegment}'.");
            }

            var propertyName = match.Groups["name"].Value;
            if (!current.TryGetProperty(propertyName, out current))
            {
                throw new InvalidOperationException($"A propriedade '{propertyName}' não existe no resultado referenciado.");
            }

            if (match.Groups["index"].Success)
            {
                if (current.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"A propriedade '{propertyName}' não é uma lista.");
                }

                var index = int.Parse(match.Groups["index"].Value);
                if (index < 0 || index >= current.GetArrayLength())
                {
                    throw new InvalidOperationException($"O índice {index} está fora dos limites da lista '{propertyName}'.");
                }

                current = current[index];
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            _ => current.GetRawText()
        };
    }
}
