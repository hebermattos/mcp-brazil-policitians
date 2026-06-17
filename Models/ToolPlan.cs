namespace McpBrazilPoliticians.Models;

public sealed record ToolPlan(
    string Tool,
    IReadOnlyDictionary<string, string?> Arguments);
