using System.Text.Json;

namespace McpBrazilPoliticians.Models;

public sealed record ChatExecutionPlan(
    IReadOnlyList<ChatExecutionStep> Steps,
    string? FinalResult = null);

public sealed record ChatExecutionStep(
    string Tool,
    IReadOnlyDictionary<string, string?> Arguments,
    string? SaveAs = null);

public sealed record ExecutedChatPlan(
    string FinalTool,
    IReadOnlyDictionary<string, object?> FinalArguments,
    JsonElement FinalData,
    IReadOnlyDictionary<string, JsonElement> StepResults);
