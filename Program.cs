using McpBrazilPoliticians.Models;
using McpBrazilPoliticians.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting mcp-brazil-policitians");

    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    var configuration = builder.Configuration;
    var mcpEndpoint = NormalizeEndpoint(GetStringSetting(configuration, "Mcp:Endpoint", "MCP_ENDPOINT", "/mcp"));
    var chatEndpoint = NormalizeEndpoint(GetStringSetting(configuration, "Chat:Endpoint", "CHAT_ENDPOINT", "/api/chat"));
    var mcpStateless = GetBoolSetting(configuration, "Mcp:Stateless", "MCP_STATELESS", defaultValue: true);
    var allowAnyOrigin = GetBoolSetting(configuration, "Cors:AllowAnyOrigin", "CORS_ALLOW_ANY_ORIGIN", defaultValue: true);
    var exposedHeaders = GetStringArraySetting(configuration, "Cors:ExposedHeaders", ["Mcp-Session-Id"]);
    var allowedOrigins = GetStringArraySetting(configuration, "Cors:AllowedOrigins", []);

    Log.Information(
        "Effective server settings: McpEndpoint={McpEndpoint}, ChatEndpoint={ChatEndpoint}, McpStateless={McpStateless}, AllowAnyOrigin={AllowAnyOrigin}, AllowedOrigins={AllowedOrigins}, ExposedHeaders={ExposedHeaders}, ChatProvider={ChatProvider}",
        mcpEndpoint,
        chatEndpoint,
        mcpStateless,
        allowAnyOrigin,
        allowedOrigins,
        exposedHeaders,
        GetStringSetting(configuration, "Chat:Provider", "CHAT_PROVIDER", "ollama"));

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("McpCors", policy =>
        {
            if (allowAnyOrigin || allowedOrigins.Length == 0)
            {
                policy.AllowAnyOrigin();
            }
            else
            {
                policy.WithOrigins(allowedOrigins);
            }

            policy
                .AllowAnyMethod()
                .AllowAnyHeader()
                .WithExposedHeaders(exposedHeaders);
        });
    });

    builder.Services.AddTransient<ProviderPromptLoggingHandler>();
    builder.Services
        .AddHttpClient(string.Empty)
        .AddHttpMessageHandler<ProviderPromptLoggingHandler>();
    builder.Services.AddSingleton<PromptFileLogService>();
    builder.Services.AddSingleton<ChatResponseFormatterService>();
    builder.Services.AddScoped<OpenAiChatService>();

    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options =>
        {
            options.Stateless = mcpStateless;
        })
        .WithToolsFromAssembly();

    var app = builder.Build();

    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
            diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
            diagnosticContext.Set("RemoteIpAddress", httpContext.Connection.RemoteIpAddress?.ToString());
            diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString());
        };
    });

    app.UseCors("McpCors");
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapGet("/health", () => Results.Ok(new
    {
        status = "ok",
        transport = "streamable-http",
        stateless = mcpStateless,
        mcpEndpoint,
        chatEndpoint,
        clientPage = "/",
        chatProvider = GetStringSetting(configuration, "Chat:Provider", "CHAT_PROVIDER", "ollama"),
        promptFileLogging = GetBoolSetting(configuration, "Logging:PromptFile:Enabled", "LOG_PROMPT_FILE_ENABLED", defaultValue: true),
        plainTextAnswerFormatting = true
    }));

    app.MapGet("/api/client-settings", () => Results.Ok(new
    {
        chatEndpoint,
        mcpEndpoint,
        chatProvider = GetStringSetting(configuration, "Chat:Provider", "CHAT_PROVIDER", "ollama")
    }));

    app.MapPost(chatEndpoint, async (
        ChatPromptRequest request,
        OpenAiChatService chatService,
        ChatResponseFormatterService responseFormatter,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            logger.LogWarning("Chat request rejected because prompt is empty");
            return Results.BadRequest(new { error = "O prompt é obrigatório." });
        }

        logger.LogInformation("Chat endpoint received prompt. PromptLength={PromptLength}", request.Prompt.Length);
        logger.LogInformation(
            """
            ================= PROMPT RECEBIDO DA TELA =================
            {Prompt}
            ===========================================================
            """,
            request.Prompt);

        try
        {
            var response = await chatService.GetAnswerAsync(request.Prompt, cancellationToken);
            var formattedResponse = responseFormatter.Format(response);
            logger.LogInformation("Chat endpoint completed. Tool={Tool}, PromptLogFile={PromptLogFile}", formattedResponse.Tool, formattedResponse.LogFilePath);
            return Results.Ok(formattedResponse);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Chat endpoint failed with controlled error");
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chat endpoint failed with unexpected error");
            return Results.Problem("Erro inesperado ao processar o prompt.", statusCode: StatusCodes.Status500InternalServerError);
        }
    });

    app.MapMcp(mcpEndpoint);

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.Information("Application is shutting down");
    await Log.CloseAndFlushAsync();
}

static string GetStringSetting(IConfiguration configuration, string configurationKey, string environmentKey, string defaultValue)
{
    var configurationValue = configuration[configurationKey];
    if (!string.IsNullOrWhiteSpace(configurationValue))
    {
        return configurationValue;
    }

    var environmentValue = Environment.GetEnvironmentVariable(environmentKey);
    return string.IsNullOrWhiteSpace(environmentValue) ? defaultValue : environmentValue;
}

static bool GetBoolSetting(IConfiguration configuration, string configurationKey, string environmentKey, bool defaultValue)
{
    var rawValue = GetStringSetting(configuration, configurationKey, environmentKey, defaultValue.ToString());
    return bool.TryParse(rawValue, out var value) ? value : defaultValue;
}

static string[] GetStringArraySetting(IConfiguration configuration, string configurationKey, string[] defaultValue)
{
    var values = configuration
        .GetSection(configurationKey)
        .GetChildren()
        .Select(child => child.Value)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .ToArray();

    return values.Length > 0 ? values : defaultValue;
}

static string NormalizeEndpoint(string endpoint)
{
    if (string.IsNullOrWhiteSpace(endpoint))
    {
        return "/";
    }

    return endpoint.StartsWith('/') ? endpoint : "/" + endpoint;
}
