using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ZeroMcp.Relay.Config;
using ZeroMcp.Relay.Ingestion;

namespace ZeroMcp.Relay.Server;

public sealed class HttpServer(
    McpRouter router,
    RelayRuntime runtime,
    RelayConfigService configService,
    OpenApiSourceLoader specLoader)
{
    private static readonly Lazy<string> EmbeddedUiHtml = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ZeroMcp.Relay.Ui.index.html");
        if (stream is null) return FallbackHtml;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static IReadOnlyCollection<string> GetRegisteredRouteTemplates(bool enableUi)
    {
        var routes = new List<string> { "/mcp (GET)", "/mcp (POST)", "/mcp/tools", "/health" };
        if (enableUi)
        {
            routes.AddRange([
                "/", "/ui", "/ui/config",
                "/ui/apis", "/ui/apis (POST)", "/ui/apis/{name} (PUT)", "/ui/apis/{name} (DELETE)",
                "/ui/apis/toggle/{name}", "/ui/apis/test/{name}", "/ui/apis/fetch-spec",
                "/ui/tools", "/ui/tools/{name}", "/ui/tools/invoke",
                "/admin/reload"
            ]);
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
            app.MapGet("/ui", () => Results.Content(EmbeddedUiHtml.Value, "text/html"));
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
                    toolCount = state.Tools.Count,
                    authType = state.Api.Auth?.Type ?? "none"
                });

                return Results.Json(new { apis });
            });

            app.MapPost("/ui/apis", async (HttpContext context) =>
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                var root = document.RootElement;

                var apiConfig = DeserializeApiConfig(root);
                if (string.IsNullOrWhiteSpace(apiConfig.Name))
                    return Results.BadRequest(new { error = "API name is required." });
                if (string.IsNullOrWhiteSpace(apiConfig.Source))
                    return Results.BadRequest(new { error = "Source URL is required." });

                var config = await configService.LoadAsync(options.ConfigPath, cancellationToken);
                if (config.Apis.Any(a => a.Name.Equals(apiConfig.Name, StringComparison.OrdinalIgnoreCase)))
                    return Results.Conflict(new { error = $"API '{apiConfig.Name}' already exists." });

                config.Apis.Add(apiConfig);

                var validation = configService.Validate(config);
                if (!validation.IsValid)
                {
                    config.Apis.Remove(apiConfig);
                    var errors = string.Join("; ", validation.Issues
                        .Where(i => i.Severity == ValidationSeverity.Error)
                        .Select(i => i.Message));
                    return Results.BadRequest(new { error = errors });
                }

                await configService.SaveAsync(config, options.ConfigPath, cancellationToken);
                await runtime.ReloadAsync(options.ConfigPath, options.ValidateOnStart, options.Lazy, failFast: false, cancellationToken);
                return Results.Ok(new { name = apiConfig.Name, status = "added" });
            });

            app.MapPut("/ui/apis/{name}", async (HttpContext context, string name) =>
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                var root = document.RootElement;

                var config = await configService.LoadAsync(options.ConfigPath, cancellationToken);
                var existing = config.Apis.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    return Results.NotFound(new { error = $"API '{name}' not found." });

                var updated = DeserializeApiConfig(root);
                updated.Name = existing.Name;
                updated.Enabled = existing.Enabled;

                var idx = config.Apis.IndexOf(existing);
                config.Apis[idx] = updated;

                var validation = configService.Validate(config);
                if (!validation.IsValid)
                {
                    config.Apis[idx] = existing;
                    var errors = string.Join("; ", validation.Issues
                        .Where(i => i.Severity == ValidationSeverity.Error)
                        .Select(i => i.Message));
                    return Results.BadRequest(new { error = errors });
                }

                await configService.SaveAsync(config, options.ConfigPath, cancellationToken);
                await runtime.ReloadAsync(options.ConfigPath, options.ValidateOnStart, options.Lazy, failFast: false, cancellationToken);
                return Results.Ok(new { name = existing.Name, status = "updated" });
            });

            app.MapDelete("/ui/apis/{name}", async (string name) =>
            {
                var config = await configService.LoadAsync(options.ConfigPath, cancellationToken);
                var existing = config.Apis.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    return Results.NotFound(new { error = $"API '{name}' not found." });

                config.Apis.Remove(existing);
                await configService.SaveAsync(config, options.ConfigPath, cancellationToken);
                await runtime.ReloadAsync(options.ConfigPath, options.ValidateOnStart, options.Lazy, failFast: false, cancellationToken);
                return Results.Ok(new { name, status = "removed" });
            });

            app.MapPost("/ui/apis/fetch-spec", async (HttpContext context) =>
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("source", out var sourceNode))
                    return Results.BadRequest(new { error = "Missing 'source' property." });

                var source = sourceNode.GetString();
                if (string.IsNullOrWhiteSpace(source))
                    return Results.BadRequest(new { error = "Source URL is empty." });

                try
                {
                    var loadResult = await specLoader.LoadAsync(source, cancellationToken);
                    if (!loadResult.IsSuccess)
                        return Results.BadRequest(new { error = string.Join("; ", loadResult.Errors) });

                    var operationCount = loadResult.Document.Paths.Values
                        .Sum(p => p.Operations.Count);

                    return Results.Ok(new
                    {
                        title = loadResult.Document.Info?.Title ?? "Untitled",
                        version = loadResult.Document.Info?.Version ?? "",
                        pathCount = loadResult.Document.Paths.Count,
                        operationCount,
                        warnings = loadResult.Warnings
                    });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
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

    private static ApiConfig DeserializeApiConfig(JsonElement root)
    {
        var config = new ApiConfig
        {
            Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            Source = root.TryGetProperty("source", out var s) ? s.GetString() ?? "" : ""
        };

        if (root.TryGetProperty("baseUrl", out var bu) && bu.ValueKind == JsonValueKind.String)
            config.BaseUrl = bu.GetString();

        if (root.TryGetProperty("prefix", out var p) && p.ValueKind == JsonValueKind.String)
            config.Prefix = p.GetString();

        if (root.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number)
            config.Timeout = t.GetInt32();

        if (root.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            config.Auth = new AuthConfig
            {
                Type = auth.TryGetProperty("type", out var at) ? at.GetString() ?? "none" : "none"
            };
            if (auth.TryGetProperty("token", out var tok)) config.Auth.Token = tok.GetString();
            if (auth.TryGetProperty("header", out var hdr)) config.Auth.Header = hdr.GetString();
            if (auth.TryGetProperty("value", out var val)) config.Auth.Value = val.GetString();
            if (auth.TryGetProperty("parameter", out var par)) config.Auth.Parameter = par.GetString();
            if (auth.TryGetProperty("username", out var usr)) config.Auth.Username = usr.GetString();
            if (auth.TryGetProperty("password", out var pwd)) config.Auth.Password = pwd.GetString();
        }

        if (root.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in headers.EnumerateObject())
            {
                config.Headers[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        if (root.TryGetProperty("include", out var inc) && inc.ValueKind == JsonValueKind.Array)
        {
            config.Include = inc.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
        }

        if (root.TryGetProperty("exclude", out var exc) && exc.ValueKind == JsonValueKind.Array)
        {
            config.Exclude = exc.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
        }

        return config;
    }

    private const string FallbackHtml = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>ZeroMcp.Relay UI</title></head>
        <body><h1>ZeroMcp.Relay UI</h1><p>Embedded UI resource not found.</p></body>
        </html>
        """;
}
