using Microsoft.OpenApi.Models;
using ZeroMcp.Relay.Config;
using ZeroMcp.Relay.Ingestion;

namespace ZeroMcp.Relay.Server;

public sealed class LoadedApiState
{
    public required ApiConfig Api { get; init; }

    public required OpenApiDocument Document { get; set; }

    public required List<ToolDefinition> Tools { get; set; }

    public DateTimeOffset LoadedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Status { get; set; } = "ok";

    public string? Error { get; set; }
}

public sealed class ToolBinding
{
    public required ToolDefinition Tool { get; init; }

    public required ApiConfig Api { get; init; }

    public required OpenApiDocument Document { get; init; }
}
