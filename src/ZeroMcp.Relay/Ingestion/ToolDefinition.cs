namespace ZeroMcp.Relay.Ingestion;

public sealed class ToolDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string ApiName { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string HttpMethod { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public Dictionary<string, object?> InputSchema { get; init; } = [];
}

public sealed record ToolGenerationWarning(string Code, string Message, string? ApiName = null, string? OperationId = null);

public sealed class ToolGenerationResult
{
    public List<ToolDefinition> Tools { get; } = [];

    public List<ToolGenerationWarning> Warnings { get; } = [];
}
