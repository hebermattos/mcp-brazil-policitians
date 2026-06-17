namespace McpBrazilPoliticians.Models;

public sealed record McpDiagnosticTool(
    string Name,
    string Description,
    IReadOnlyList<string> Arguments);

public sealed record McpDiagnosticToolCallRequest(
    string Name,
    IReadOnlyDictionary<string, string?>? Arguments);
