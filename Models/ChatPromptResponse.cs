using System.Text.Json;

namespace McpBrazilPoliticians.Models;

public sealed record ChatPromptResponse(
    string Answer,
    string? Tool,
    IReadOnlyDictionary<string, object?>? Arguments,
    JsonElement? Data,
    string? LogFilePath);
