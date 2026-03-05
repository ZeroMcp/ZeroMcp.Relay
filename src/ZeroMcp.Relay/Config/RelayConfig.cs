using System.Text.Json.Serialization;

namespace ZeroMcp.Relay.Config;

public sealed class RelayConfig
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; } = "https://zeromcp.dev/schemas/relay.config.json";

    public string ServerName { get; set; } = "ZeroMcp.Relay";

    public string ServerVersion { get; set; } = "1.0.0";

    public int DefaultTimeout { get; set; } = 30;

    public List<ApiConfig> Apis { get; set; } = [];
}

public sealed class ApiConfig
{
    public string Name { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string? BaseUrl { get; set; }

    public string? Prefix { get; set; }

    public bool Enabled { get; set; } = true;

    public int? Timeout { get; set; }

    public AuthConfig? Auth { get; set; }

    public Dictionary<string, string> Headers { get; set; } = [];

    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];
}

public sealed class AuthConfig
{
    public string Type { get; set; } = "none";

    public string? Token { get; set; }

    public string? Header { get; set; }

    public string? Value { get; set; }

    public string? Parameter { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }
}
