using Microsoft.OpenApi.Models;

namespace ZeroMcp.Relay.Ingestion;

public sealed class OpenApiLoadResult
{
    public required OpenApiDocument Document { get; init; }

    public required string Source { get; init; }

    public required DateTimeOffset LoadedAtUtc { get; init; }

    public List<string> Errors { get; } = [];

    public List<string> Warnings { get; } = [];

    public bool IsSuccess => Errors.Count == 0;
}
