using System.Text.Json;
using System.Text.RegularExpressions;
using McpBrazilPoliticians.Models;

namespace McpBrazilPoliticians.Services;

public sealed class ChatPlanExecutorService
{
    private static readonly Regex TemplateRegex = new(
        @"^\{\{(?<result>[A-Za-z0-9_]+)\.(?<path>.+)\}\}$",
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
        ChatExecutionStep? finalStep = null;
        IReadOnlyDictionary<string, string?> finalResolvedArguments = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in plan.Steps)
        {
            var resolvedArguments = ResolveArguments(step.Arguments, stepResults);

            _logger.LogInformation(
                "Executing chained chat plan step. Tool={Tool}, SaveAs={SaveAs}, Arguments={Arguments}",
                step.Tool,
                step.SaveAs,
                JsonSerializer.Serialize(resolvedArguments));

            using var result = await _toolExecutionService.ExecuteAsync(step.Tool, resolvedArguments, cancellationToken);
            var clonedResult = result.RootElement.Clone();

            if (!string.IsNullOrWhiteSpace(step.SaveAs))
            {
                stepResults[step.SaveAs] = clonedResult;
            }

            finalStep = step;
            finalResolvedArguments = resolvedArguments;
        }

        var finalResultKey = plan.FinalResult;
        JsonElement finalData;

        if (!string.IsNullOrWhiteSpace(finalResultKey))
        {
            if (!stepResults.TryGetValue(finalResultKey, out finalData))
            {
                throw new InvalidOperationException($"O resultado final '{finalResultKey}' não existe no contexto do plano.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(finalStep?.SaveAs) && stepResults.TryGetValue(finalStep.SaveAs, out finalData))
        {
            // Usa o último resultado salvo.
        }
        else
        {
            throw new InvalidOperationException("Não foi possível determinar o resultado final do plano encadeado.");
        }

        return new ExecutedChatPlan(
            finalStep!.Tool,
            finalResolvedArguments.ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.OrdinalIgnoreCase),
            finalData,
            stepResults);
    }

    private Dictionary<string, string?> ResolveArguments(
        IReadOnlyDictionary<string, string?> arguments,
        IReadOnlyDictionary<string, JsonElement> context)
    {
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var argument in arguments)
        {
            resolved[argument.Key] = ResolveTemplate(argument.Value, context);
        }

        return resolved;
    }

    private string? ResolveTemplate(string? value, IReadOnlyDictionary<string, JsonElement> context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var match = TemplateRegex.Match(value.Trim());
        if (!match.Success)
        {
            return value;
        }

        var resultName = match.Groups["result"].Value;
        var path = match.Groups["path"].Value;

        if (!context.TryGetValue(resultName, out var json))
        {
            throw new InvalidOperationException($"Resultado '{resultName}' não existe no contexto do plano.");
        }

        return _jsonPathResolver.Resolve(json, path);
    }
}
