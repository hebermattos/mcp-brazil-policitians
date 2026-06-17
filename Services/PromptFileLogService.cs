using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace McpBrazilPoliticians.Services;

public sealed class PromptFileLogService
{
    private const string DefaultDirectory = "logs/prompts";
    private const int DefaultMaxBodyChars = 10000;

    private readonly IConfiguration _configuration;
    private readonly ILogger<PromptFileLogService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public PromptFileLogService(IConfiguration configuration, ILogger<PromptFileLogService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public PromptFileLogContext? Start(string operationId, string prompt, string provider)
    {
        if (!GetBool("Logging:PromptFile:Enabled", "LOG_PROMPT_FILE_ENABLED", defaultValue: true))
        {
            return null;
        }

        try
        {
            var baseDirectory = GetString("Logging:PromptFile:Directory", "LOG_PROMPT_FILE_DIRECTORY", DefaultDirectory);
            var now = DateTimeOffset.Now;
            var dayDirectory = Path.Combine(baseDirectory, now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dayDirectory);

            var promptSlug = CreateSlug(prompt, maxLength: 50);
            var fileName = $"{now:HHmmssfff}_{operationId}_{promptSlug}.log";
            var filePath = Path.Combine(dayDirectory, fileName);

            var context = new PromptFileLogContext(
                OperationId: operationId,
                FilePath: filePath,
                StartedAt: now,
                Provider: provider);

            Append(context, "prompt.started", new
            {
                operationId,
                provider,
                promptLength = prompt.Length,
                prompt = GetOptionalLogText("Logging:PromptFile:IncludePromptContent", "LOG_PROMPT_FILE_INCLUDE_PROMPT_CONTENT", prompt),
                startedAt = now
            });

            return context;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create prompt log file. OperationId={OperationId}", operationId);
            return null;
        }
    }

    public void Append(PromptFileLogContext? context, string stage, object? data = null)
    {
        if (context is null)
        {
            return;
        }

        try
        {
            var entry = new
            {
                timestamp = DateTimeOffset.Now,
                elapsedMs = (long)(DateTimeOffset.Now - context.StartedAt).TotalMilliseconds,
                operationId = context.OperationId,
                provider = context.Provider,
                stage,
                data
            };

            var text = new StringBuilder()
                .AppendLine("================================================================================")
                .AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {stage}")
                .AppendLine("--------------------------------------------------------------------------------")
                .AppendLine(JsonSerializer.Serialize(entry, _jsonOptions))
                .AppendLine()
                .ToString();

            File.AppendAllText(context.FilePath, text, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not append to prompt log file. OperationId={OperationId}, Stage={Stage}, FilePath={FilePath}", context.OperationId, stage, context.FilePath);
        }
    }

    public void Complete(PromptFileLogContext? context, object? data = null)
    {
        if (context is null)
        {
            return;
        }

        Append(context, "prompt.completed", data);
    }

    public void Fail(PromptFileLogContext? context, Exception exception, object? data = null)
    {
        if (context is null)
        {
            return;
        }

        Append(context, "prompt.failed", new
        {
            exceptionType = exception.GetType().FullName,
            exception.Message,
            exception.StackTrace,
            data
        });
    }

    public string? GetOptionalLogText(string configurationKey, string environmentKey, string value)
    {
        return GetBool(configurationKey, environmentKey, defaultValue: true)
            ? Truncate(value)
            : null;
    }

    public string Truncate(string value)
    {
        var maxChars = GetInt("Logging:PromptFile:MaxBodyChars", "LOG_PROMPT_FILE_MAX_BODY_CHARS", DefaultMaxBodyChars, min: 100, max: 1_000_000);
        return value.Length <= maxChars ? value : value[..maxChars] + "\n... prompt log truncated ...";
    }

    private string GetString(string configurationKey, string environmentKey, string defaultValue)
    {
        var configurationValue = _configuration[configurationKey];
        if (!string.IsNullOrWhiteSpace(configurationValue))
        {
            return configurationValue;
        }

        var environmentValue = Environment.GetEnvironmentVariable(environmentKey);
        return string.IsNullOrWhiteSpace(environmentValue) ? defaultValue : environmentValue;
    }

    private int GetInt(string configurationKey, string environmentKey, int defaultValue, int min, int max)
    {
        var rawValue = GetString(configurationKey, environmentKey, defaultValue.ToString());
        return int.TryParse(rawValue, out var value) ? Math.Clamp(value, min, max) : defaultValue;
    }

    private bool GetBool(string configurationKey, string environmentKey, bool defaultValue)
    {
        var rawValue = GetString(configurationKey, environmentKey, defaultValue.ToString());
        return bool.TryParse(rawValue, out var value) ? value : defaultValue;
    }

    private static string CreateSlug(string value, int maxLength)
    {
        var normalized = value.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, "[^a-z0-9áàâãéèêíïóôõöúçñ]+", "-");
        normalized = normalized.Trim('-');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "prompt";
        }

        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].Trim('-');
    }
}

public sealed record PromptFileLogContext(
    string OperationId,
    string FilePath,
    DateTimeOffset StartedAt,
    string Provider);
