using McpBrazilPoliticians.Models;
using McpBrazilPoliticians.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("McpCors", policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("Mcp-Session-Id");
    });
});

builder.Services.AddHttpClient();
builder.Services.AddScoped<OpenAiChatService>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        // This project exposes simple request/response tools and does not need
        // server-side MCP sessions. Stateless mode also avoids requiring the
        // Mcp-Session-Id header when testing the endpoint from a browser/client.
        options.Stateless = true;
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
    stateless = true,
    mcpEndpoint = "/mcp",
    chatEndpoint = "/api/chat",
    clientPage = "/"
}));

app.MapPost("/api/chat", async (
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

app.MapMcp("/mcp");

await app.RunAsync();
