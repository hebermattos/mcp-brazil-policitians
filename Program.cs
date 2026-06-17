using McpBrazilPoliticians.Models;
using McpBrazilPoliticians.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

var configuration = builder.Configuration;
var mcpEndpoint = NormalizeEndpoint(GetStringSetting(configuration, "Mcp:Endpoint", "MCP_ENDPOINT", "/mcp"));
var chatEndpoint = NormalizeEndpoint(GetStringSetting(configuration, "Chat:Endpoint", "CHAT_ENDPOINT", "/api/chat"));
var mcpStateless = GetBoolSetting(configuration, "Mcp:Stateless", "MCP_STATELESS", defaultValue: true);
var allowAnyOrigin = GetBoolSetting(configuration, "Cors:AllowAnyOrigin", "CORS_ALLOW_ANY_ORIGIN", defaultValue: true);
var exposedHeaders = GetStringArraySetting(configuration, "Cors:ExposedHeaders", ["Mcp-Session-Id"]);
var allowedOrigins = GetStringArraySetting(configuration, "Cors:AllowedOrigins", []);

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

builder.Services.AddHttpClient();
builder.Services.AddScoped<OpenAiChatService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = mcpStateless;
    })
    .WithToolsFromAssembly();

var app = builder.Build();

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
    chatProvider = GetStringSetting(configuration, "Chat:Provider", "CHAT_PROVIDER", "ollama")
}));

app.MapPost(chatEndpoint, async (
    ChatPromptRequest request,
    OpenAiChatService chatService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.BadRequest(new { error = "O prompt é obrigatório." });
    }

    try
    {
        var response = await chatService.GetAnswerAsync(request.Prompt, cancellationToken);
        return Results.Ok(response);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapMcp(mcpEndpoint);

await app.RunAsync();

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
    var section = configuration.GetSection(configurationKey);
    var values = section.Get<string[]>();

    return values is { Length: > 0 }
        ? values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
        : defaultValue;
}

static string NormalizeEndpoint(string endpoint)
{
    if (string.IsNullOrWhiteSpace(endpoint))
    {
        return "/";
    }

    return endpoint.StartsWith('/') ? endpoint : "/" + endpoint;
}
