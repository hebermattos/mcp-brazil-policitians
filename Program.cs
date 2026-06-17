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
    var mcpPageEndpoint = NormalizeEndpoint(GetStringSetting(configuration, "Mcp:PageEndpoint", "MCP_PAGE_ENDPOINT", "/mcp"));
    var mcpEndpoint = NormalizeEndpoint(GetStringSetting(configuration, "Mcp:Endpoint", "MCP_ENDPOINT", "/mcp-server"));
    var chatEndpoint = NormalizeEndpoint(GetStringSetting(configuration, "Chat:Endpoint", "CHAT_ENDPOINT", "/api/chat"));
    var mcpStateless = GetBoolSetting(configuration, "Mcp:Stateless", "MCP_STATELESS", defaultValue: true);
    var allowAnyOrigin = GetBoolSetting(configuration, "Cors:AllowAnyOrigin", "CORS_ALLOW_ANY_ORIGIN", defaultValue: true);
    var exposedHeaders = GetStringArraySetting(configuration, "Cors:ExposedHeaders", ["Mcp-Session-Id"]);
    var allowedOrigins = GetStringArraySetting(configuration, "Cors:AllowedOrigins", []);

    if (string.Equals(mcpPageEndpoint, mcpEndpoint, StringComparison.OrdinalIgnoreCase))
    {
        Log.Warning(
            "MCP page endpoint and MCP server endpoint were both configured as {Endpoint}. Moving MCP server endpoint to /mcp-server to avoid route conflicts.",
            mcpEndpoint);
        mcpEndpoint = "/mcp-server";
    }

    Log.Information(
        "Effective server settings: McpPageEndpoint={McpPageEndpoint}, McpEndpoint={McpEndpoint}, ChatEndpoint={ChatEndpoint}, McpStateless={McpStateless}, AllowAnyOrigin={AllowAnyOrigin}, AllowedOrigins={AllowedOrigins}, ExposedHeaders={ExposedHeaders}, ChatProvider={ChatProvider}",
        mcpPageEndpoint,
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
    builder.Services.AddScoped<CamaraJsonPathResolver>();
    builder.Services.AddScoped<CamaraToolExecutionService>();
    builder.Services.AddScoped<ChatPlanExecutorService>();
    builder.Services.AddScoped<LatestPropositionVotingQueryService>();
    builder.Services.AddScoped<DirectChatQueryService>();
    builder.Services.AddScoped<ChainedOpenAiChatService>();

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

    app.MapGet(mcpPageEndpoint, () => Results.File("wwwroot/mcp.html", "text/html"));

    app.MapGet("/health", () => Results.Ok(new
    {
        status = "ok",
        transport = "streamable-http",
        stateless = mcpStateless,
        mcpPageEndpoint,
        mcpEndpoint,
        chatEndpoint,
        clientPage = "/",
        mcpPage = mcpPageEndpoint,
        chatProvider = GetStringSetting(configuration, "Chat:Provider", "CHAT_PROVIDER", "ollama"),
        promptFileLogging = GetBoolSetting(configuration, "Logging:PromptFile:Enabled", "LOG_PROMPT_FILE_ENABLED", defaultValue: true),
        plainTextAnswerFormatting = true
    }));

    app.MapGet("/api/client-settings", () => Results.Ok(new
    {
        chatEndpoint,
        mcpEndpoint,
        mcpPageEndpoint,
        chatProvider = GetStringSetting(configuration, "Chat:Provider", "CHAT_PROVIDER", "ollama")
    }));

    app.MapGet("/api/mcp/tools", () => Results.Ok(McpDiagnosticToolCatalog.All));

    app.MapPost("/api/mcp/tools/call", async (
        McpDiagnosticToolCallRequest request,
        CamaraToolExecutionService toolExecutionService,
        ChatResponseFormatterService responseFormatter,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = "O nome da ferramenta é obrigatório." });
        }

        var arguments = request.Arguments ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        logger.LogInformation(
            "Executing MCP diagnostic tool call. Tool={Tool}, Arguments={Arguments}",
            request.Name,
            arguments);

        var json = await toolExecutionService.ExecuteAsync(request.Name, arguments, cancellationToken);
        var response = new ChatPromptResponse(
            "Consulta executada pela tela de diagnóstico MCP.",
            request.Name,
            arguments.ToDictionary(item => item.Key, item => (object?)item.Value, StringComparer.OrdinalIgnoreCase),
            json.RootElement.Clone(),
            null);

        return Results.Ok(responseFormatter.Format(response));
    });

    app.MapPost(chatEndpoint, async (
        ChatPromptRequest request,
        ChainedOpenAiChatService chatService,
        LatestPropositionVotingQueryService latestPropositionVotingQueryService,
        DirectChatQueryService directQueryService,
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
            var latestPropositionVotingResponse = await latestPropositionVotingQueryService.TryHandleAsync(request.Prompt, cancellationToken);
            if (latestPropositionVotingResponse is not null)
            {
                var formattedResponse = responseFormatter.Format(latestPropositionVotingResponse);
                logger.LogInformation(
                    "Chat endpoint completed with latest proposition voting handler. Tool={Tool}, Arguments={Arguments}",
                    formattedResponse.Tool,
                    formattedResponse.Arguments);

                return Results.Ok(formattedResponse);
            }

            var directResponse = await directQueryService.TryHandleAsync(request.Prompt, cancellationToken);
            if (directResponse is not null)
            {
                var formattedDirectResponse = responseFormatter.Format(directResponse);
                logger.LogInformation(
                    "Chat endpoint completed with direct handler. Tool={Tool}, Arguments={Arguments}",
                    formattedDirectResponse.Tool,
                    formattedDirectResponse.Arguments);

                return Results.Ok(formattedDirectResponse);
            }

            var response = await chatService.GetAnswerAsync(request.Prompt, cancellationToken);
            var formattedResponseFromModel = responseFormatter.Format(response);
            logger.LogInformation("Chat endpoint completed. Tool={Tool}, PromptLogFile={PromptLogFile}", formattedResponseFromModel.Tool, formattedResponseFromModel.LogFilePath);
            return Results.Ok(formattedResponseFromModel);
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
