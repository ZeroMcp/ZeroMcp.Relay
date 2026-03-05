namespace ZeroMcp.Relay.Relay;

public sealed class RelayDispatchResult
{
    public bool IsError { get; init; }

    public int? StatusCode { get; init; }

    public string Content { get; init; } = string.Empty;
}
