using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace ZeroMcp.Relay.Server;

public sealed class HttpServer(McpRouter router, RelayRuntime runtime)
{
    public async Task<int> RunAsync(RunOptions options, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();

        var app = builder.Build();
        app.Urls.Add($"http://{options.Host}:{options.Port}");

        app.MapGet("/mcp", () => Results.Ok(new
        {
            name = runtime.Config.ServerName,
            version = runtime.Config.ServerVersion
        }));

        app.MapPost("/mcp", async context =>
        {
            using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            var response = await router.HandleAsync(request.RootElement, cancellationToken);
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(response), cancellationToken);
        });

        app.MapGet("/mcp/tools", async () =>
        {
            await runtime.EnsureApisLoadedAsync(validateOnStart: false, failFast: false, cancellationToken);
            var tools = runtime.Tools.Select(tool => new
            {
                name = tool.Name,
                description = tool.Description,
                inputSchema = tool.InputSchema
            });

            return Results.Ok(new { tools });
        });

        app.MapGet("/health", () => Results.Ok(runtime.BuildHealthResponse()));

        await app.RunAsync(cancellationToken);
        return 0;
    }
}
