using System.Text.Json;

namespace ZeroMcp.Relay.Server;

public sealed class McpRouter(RelayRuntime runtime)
{
    public async Task<object> HandleAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        var id = request.TryGetProperty("id", out var requestId) ? requestId : default;
        var method = request.TryGetProperty("method", out var methodNode) ? methodNode.GetString() : null;

        if (string.IsNullOrWhiteSpace(method))
        {
            return BuildError(id, -32600, "Invalid request: missing method.");
        }

        try
        {
            return method switch
            {
                "initialize" => BuildSuccess(id, new
                {
                    serverName = runtime.Config.ServerName,
                    serverVersion = runtime.Config.ServerVersion,
                    capabilities = new
                    {
                        tools = new { }
                    }
                }),
                "tools/list" => await HandleToolsListAsync(id, cancellationToken),
                "tools/call" => await HandleToolCallAsync(id, request, cancellationToken),
                _ => BuildError(id, -32601, $"Method '{method}' not found.")
            };
        }
        catch (Exception ex)
        {
            return BuildError(id, -32000, ex.Message);
        }
    }

    private async Task<object> HandleToolsListAsync(JsonElement id, CancellationToken cancellationToken)
    {
        await runtime.EnsureApisLoadedAsync(validateOnStart: false, failFast: false, cancellationToken);

        var tools = runtime.Tools.Select(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            inputSchema = tool.InputSchema
        });

        return BuildSuccess(id, new { tools });
    }

    private async Task<object> HandleToolCallAsync(JsonElement id, JsonElement request, CancellationToken cancellationToken)
    {
        if (!request.TryGetProperty("params", out var parameters))
        {
            return BuildError(id, -32602, "Missing params.");
        }

        if (!parameters.TryGetProperty("name", out var nameNode))
        {
            return BuildError(id, -32602, "Missing tool name.");
        }

        var toolName = nameNode.GetString();
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return BuildError(id, -32602, "Tool name must be non-empty.");
        }

        JsonElement? arguments = null;
        if (parameters.TryGetProperty("arguments", out var argsNode))
        {
            arguments = argsNode;
        }

        var dispatchResult = await runtime.DispatchAsync(toolName, arguments, cancellationToken);
        return BuildSuccess(id, new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = dispatchResult.Content
                }
            },
            isError = dispatchResult.IsError
        });
    }

    private static object BuildSuccess(JsonElement id, object result)
    {
        return new
        {
            jsonrpc = "2.0",
            id = id.ValueKind == JsonValueKind.Undefined ? (object?)null : JsonSerializer.Deserialize<object>(id.GetRawText()),
            result
        };
    }

    private static object BuildError(JsonElement id, int code, string message)
    {
        return new
        {
            jsonrpc = "2.0",
            id = id.ValueKind == JsonValueKind.Undefined ? (object?)null : JsonSerializer.Deserialize<object>(id.GetRawText()),
            error = new
            {
                code,
                message
            }
        };
    }
}
