using System.Text.Json;
using System.Text.RegularExpressions;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class ChatPlanExecutorService
{
    private static readonly Regex TemplateRegex = new(
        @"\{\{(?<result>[A-Za-z0-9_]+)(?:\.(?<path>[^}]+))?\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly CamaraToolExecutionService _toolExecutionService;
    private readonly CamaraJsonPathResolver _jsonPathResolver;
    private readonly ILogger<ChatPlanExecutorService> _logger;

    public ChatPlanExecutorService(
        CamaraToolExecutionService toolExecutionService,
        CamaraJsonPathResolver jsonPathResolver,
        ILogger<ChatPlanExecutorService> logger)
    {
        _toolExecutionService = toolExecutionService;
        _jsonPathResolver = jsonPathResolver;
        _logger = logger;
    }

    public async Task<ExecutedChatPlan> ExecuteAsync(ChatExecutionPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Steps.Count == 0)
        {
            throw new InvalidOperationException("O plano de execução não possui etapas.");
        }

        var stepResults = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var stepTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stepArguments = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        var lastResultKey = string.Empty;

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            var stepKey = $"step{index + 1}";
            var resolvedArguments = ResolveArguments(step.Arguments, stepResults);

            _logger.LogInformation(
                "Executing chained chat plan step. StepIndex={StepIndex}, StepKey={StepKey}, Tool={Tool}, SaveAs={SaveAs}, Arguments={Arguments}",
                index + 1,
                stepKey,
                step.Tool,
                step.SaveAs,
                JsonSerializer.Serialize(resolvedArguments));

            using var result = await _toolExecutionService.ExecuteAsync(step.Tool, resolvedArguments, cancellationToken);
            var clonedResult = result.RootElement.Clone();

            SaveStepResult(stepKey, step.Tool, resolvedArguments, clonedResult, stepResults, stepTools, stepArguments);
            lastResultKey = stepKey;

            if (!string.IsNullOrWhiteSpace(step.SaveAs))
            {
                SaveStepResult(step.SaveAs, step.Tool, resolvedArguments, clonedResult, stepResults, stepTools, stepArguments);
                lastResultKey = step.SaveAs;
            }
        }

        var finalResultKey = !string.IsNullOrWhiteSpace(plan.FinalResult)
            ? plan.FinalResult
            : lastResultKey;

        if (string.IsNullOrWhiteSpace(finalResultKey))
        {
            throw new InvalidOperationException("Não foi possível determinar o resultado final do plano encadeado.");
        }

        if (!stepResults.TryGetValue(finalResultKey, out var finalData))
        {
            throw new InvalidOperationException($"O resultado final '{finalResultKey}' não existe no contexto do plano.");
        }

        var finalTool = stepTools.TryGetValue(finalResultKey, out var savedTool)
            ? savedTool
            : plan.Steps[^1].Tool;

        var finalResolvedArguments = stepArguments.TryGetValue(finalResultKey, out var savedArguments)
            ? savedArguments
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        return new ExecutedChatPlan(
            finalTool,
            finalResolvedArguments.ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.OrdinalIgnoreCase),
            finalData,
            stepResults);
    }

    private static void SaveStepResult(
        string key,
        string tool,
        IReadOnlyDictionary<string, string?> resolvedArguments,
        JsonElement result,
        IDictionary<string, JsonElement> stepResults,
        IDictionary<string, string> stepTools,
        IDictionary<string, IReadOnlyDictionary<string, string?>> stepArguments)
    {
        stepResults[key] = result;
        stepTools[key] = tool;
        stepArguments[key] = resolvedArguments.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, string?> ResolveArguments(
        IReadOnlyDictionary<string, string?> arguments,
        IReadOnlyDictionary<string, JsonElement> context)
    {
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var argument in arguments)
        {
            resolved[argument.Key] = ResolveTemplates(argument.Value, context);
        }

        return resolved;
    }

    private string? ResolveTemplates(string? value, IReadOnlyDictionary<string, JsonElement> context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return TemplateRegex.Replace(value, match => ResolveTemplateMatch(match, context) ?? string.Empty);
    }

    private string? ResolveTemplateMatch(Match match, IReadOnlyDictionary<string, JsonElement> context)
    {
        var resultName = match.Groups["result"].Value;
        var path = match.Groups["path"].Success ? match.Groups["path"].Value : null;

        if (!context.TryGetValue(resultName, out var json))
        {
            throw new InvalidOperationException($"Resultado '{resultName}' não existe no contexto do plano.");
        }

        return string.IsNullOrWhiteSpace(path)
            ? JsonElementToString(json)
            : _jsonPathResolver.Resolve(json, path);
    }

    private static string JsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            _ => element.GetRawText()
        };
    }
}
