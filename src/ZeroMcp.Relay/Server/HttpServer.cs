using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ZeroMcp.Relay.Config;

namespace ZeroMcp.Relay.Server;

public sealed class HttpServer(McpRouter router, RelayRuntime runtime, RelayConfigService configService)
{
    public static IReadOnlyCollection<string> GetRegisteredRouteTemplates(bool enableUi)
    {
        var routes = new List<string> { "/mcp (GET)", "/mcp (POST)", "/mcp/tools", "/health" };
        if (enableUi)
        {
            routes.AddRange(["/", "/ui", "/ui/config", "/ui/apis", "/ui/apis/toggle/{name}", "/ui/apis/test/{name}", "/ui/tools", "/ui/tools/{name}", "/ui/tools/invoke", "/admin/reload"]);
        }

        return routes;
    }

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

        if (options.EnableUi)
        {
            app.MapGet("/", () => Results.Redirect("/ui"));
            app.MapGet("/ui", () => Results.Content(GetUiPlaceholderHtml(), "text/html"));
            app.MapGet("/ui/config", () =>
            {
                var masked = ConfigMasking.CreateMaskedCopy(runtime.Config);
                return Results.Json(masked);
            });
            app.MapGet("/ui/apis", () =>
            {
                var apis = runtime.ApiStates.Values.Select(state => new
                {
                    name = state.Api.Name,
                    source = state.Api.Source,
                    enabled = state.Api.Enabled,
                    status = state.Status,
                    error = state.Error,
                    toolCount = state.Tools.Count
                });

                return Results.Json(new { apis });
            });
            app.MapPost("/ui/apis/toggle/{name}", async (HttpContext context, string name) =>
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                var enabled = document.RootElement.TryGetProperty("enabled", out var enabledNode) && enabledNode.GetBoolean();
                var config = await configService.LoadAsync(options.ConfigPath, cancellationToken);
                var api = config.Apis.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (api is null)
                {
                    return Results.NotFound(new { error = $"API '{name}' not found." });
                }

                api.Enabled = enabled;
                await configService.SaveAsync(config, options.ConfigPath, cancellationToken);
                await runtime.ReloadAsync(options.ConfigPath, options.ValidateOnStart, options.Lazy, failFast: false, cancellationToken);
                return Results.Ok(new { name, enabled });
            });
            app.MapPost("/ui/apis/test/{name}", async (string name) =>
            {
                await runtime.EnsureApiLoadedAsync(name, cancellationToken);
                if (!runtime.ApiStates.TryGetValue(name, out var state))
                {
                    return Results.NotFound(new { error = $"API '{name}' not found." });
                }

                return Results.Ok(new
                {
                    name,
                    status = state.Status,
                    error = state.Error,
                    toolCount = state.Tools.Count
                });
            });
            app.MapGet("/ui/tools", async (string? api) =>
            {
                await runtime.EnsureApisLoadedAsync(validateOnStart: false, failFast: false, cancellationToken);
                var tools = runtime.Tools
                    .Where(tool => string.IsNullOrWhiteSpace(api) || tool.ApiName.Equals(api, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(tool => new
                    {
                        tool.Name,
                        tool.Description,
                        tool.ApiName,
                        tool.HttpMethod,
                        tool.Path
                    });
                return Results.Json(new { tools });
            });
            app.MapGet("/ui/tools/{name}", async (string name) =>
            {
                await runtime.EnsureApisLoadedAsync(validateOnStart: false, failFast: false, cancellationToken);
                var tool = runtime.Tools.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                return tool is null
                    ? Results.NotFound(new { error = $"Tool '{name}' not found." })
                    : Results.Json(tool);
            });
            app.MapPost("/ui/tools/invoke", async (HttpContext context) =>
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("name", out var nameNode))
                {
                    return Results.BadRequest(new { error = "Missing tool name." });
                }

                var toolName = nameNode.GetString();
                JsonElement? args = null;
                if (document.RootElement.TryGetProperty("arguments", out var argNode))
                {
                    args = argNode;
                }

                var result = await runtime.DispatchAsync(toolName ?? string.Empty, args, cancellationToken);
                return Results.Json(result);
            });
            app.MapPost("/admin/reload", async () =>
            {
                await runtime.ReloadAsync(options.ConfigPath, options.ValidateOnStart, options.Lazy, failFast: false, cancellationToken);
                return Results.Ok(new { status = "reloaded" });
            });
        }

        await app.RunAsync();
        return 0;
    }

    private static string GetUiPlaceholderHtml()
    {
        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>ZeroMcp.Relay UI</title>
</head>
<body>
  <h1>ZeroMcp.Relay UI</h1>
  <p>UI API is enabled. Use /ui/config, /ui/apis, /ui/tools.</p>
</body>
</html>
""";
    }
}
