using System.Net.Sockets;
using System.Text;

namespace ZeroMcp.Relay.Ingestion;

/// <summary>
/// Probes a host for open ports that expose an OpenAPI/Swagger specification on
/// well-known document URLs, so discovered specs can be listed and added as APIs.
/// </summary>
public sealed class SwaggerScanner
{
    private readonly OpenApiSourceLoader _loader;
    private readonly HttpClient _httpClient;

    public static readonly IReadOnlyList<int> DefaultPorts =
    [
        80, 443, 3000, 5000, 5001, 8000, 8080, 8081, 8443, 9000, 9090
    ];

    // Common locations where frameworks publish their OpenAPI/Swagger document.
    public static readonly IReadOnlyList<string> DefaultPaths =
    [
        "/swagger/v1/swagger.json",
        "/swagger/v2/swagger.json",
        "/swagger.json",
        "/openapi.json",
        "/openapi/v1.json",
        "/v1/openapi.json",
        "/api/openapi.json",
        "/api-docs",
        "/v2/api-docs",
        "/v3/api-docs",
        "/swagger/docs/v1"
    ];

    public SwaggerScanner(OpenApiSourceLoader loader)
        : this(loader, CreateDefaultHttpClient())
    {
    }

    public SwaggerScanner(OpenApiSourceLoader loader, HttpClient httpClient)
    {
        _loader = loader;
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<SwaggerScanResult>> ScanAsync(
        SwaggerScanOptions options,
        CancellationToken cancellationToken = default)
    {
        var host = string.IsNullOrWhiteSpace(options.Host) ? "localhost" : options.Host.Trim();
        var ports = (options.Ports is { Count: > 0 } ? options.Ports : DefaultPorts).Distinct().ToList();
        var paths = options.Paths is { Count: > 0 } ? options.Paths : DefaultPaths;
        var schemes = options.Schemes is { Count: > 0 } ? options.Schemes : ["http", "https"];
        var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromSeconds(2);
        var concurrency = options.MaxConcurrency > 0 ? options.MaxConcurrency : 16;

        var gate = new SemaphoreSlim(concurrency);
        var results = new List<SwaggerScanResult>();
        var sync = new object();

        var portTasks = ports.Select(async port =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var found = await ScanPortAsync(host, port, schemes, paths, timeout, cancellationToken);
                if (found.Count > 0)
                {
                    lock (sync)
                    {
                        results.AddRange(found);
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(portTasks);

        return results
            .OrderBy(r => r.Port)
            .ThenBy(r => r.Scheme, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<SwaggerScanResult>> ScanPortAsync(
        string host,
        int port,
        IReadOnlyList<string> schemes,
        IReadOnlyList<string> paths,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var found = new List<SwaggerScanResult>();

        if (!await IsPortOpenAsync(host, port, timeout, cancellationToken))
        {
            return found;
        }

        foreach (var scheme in schemes)
        {
            foreach (var path in paths)
            {
                var url = $"{scheme}://{host}:{port}{path}";
                var result = await ProbeAsync(url, host, port, scheme, path, timeout, cancellationToken);
                if (result is not null)
                {
                    // One spec per scheme is enough to surface the host for adding.
                    found.Add(result);
                    break;
                }
            }
        }

        return found;
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await tcp.ConnectAsync(host, port, cts.Token);
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task<SwaggerScanResult?> ProbeAsync(
        string url,
        string host,
        int port,
        string scheme,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cts.Token);
            if (!LooksLikeSpec(content))
            {
                return null;
            }

            var parsed = _loader.Parse(content, url);
            if (!parsed.IsSuccess)
            {
                return null;
            }

            var operationCount = parsed.Document.Paths?.Values.Sum(p => p.Operations.Count) ?? 0;
            var pathCount = parsed.Document.Paths?.Count ?? 0;
            var title = parsed.Document.Info?.Title;

            // Guard against arbitrary JSON that happens to parse without errors.
            if (operationCount == 0 && pathCount == 0 && string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return new SwaggerScanResult
            {
                Url = url,
                Host = host,
                Port = port,
                Scheme = scheme,
                Path = path,
                Title = string.IsNullOrWhiteSpace(title) ? null : title,
                Version = parsed.Document.Info?.Version,
                PathCount = pathCount,
                OperationCount = operationCount,
                SuggestedName = SuggestName(title, host, port)
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeSpec(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        // Cheap pre-filter before committing to a full OpenAPI parse.
        var head = content.Length > 4096 ? content[..4096] : content;
        return head.Contains("openapi", StringComparison.OrdinalIgnoreCase)
            || head.Contains("swagger", StringComparison.OrdinalIgnoreCase);
    }

    public static string SuggestName(string? title, string host, int port)
    {
        var slug = Slugify(title);
        if (!string.IsNullOrEmpty(slug))
        {
            return slug;
        }

        var hostSlug = Slugify(host);
        return string.IsNullOrEmpty(hostSlug) ? $"api_{port}" : $"{hostSlug}_{port}";
    }

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousUnderscore = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousUnderscore = false;
            }
            else if (!previousUnderscore && builder.Length > 0)
            {
                builder.Append('_');
                previousUnderscore = true;
            }
        }

        return builder.ToString().Trim('_');
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            // Localhost/dev servers frequently use self-signed certificates.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = true
        };

        return new HttpClient(handler);
    }
}

public static class PortListParser
{
    /// <summary>
    /// Parses a comma-separated list of ports and inclusive ranges
    /// (e.g. "8080,5000-5010,443") into a sorted, de-duplicated list.
    /// </summary>
    public static IReadOnlyList<int> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var ports = new SortedSet<int>();
        foreach (var token in value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = token.IndexOf('-');
            if (dash > 0)
            {
                var startText = token[..dash];
                var endText = token[(dash + 1)..];
                if (TryParsePort(startText, out var start) && TryParsePort(endText, out var end) && start <= end)
                {
                    for (var port = start; port <= end; port++)
                    {
                        ports.Add(port);
                    }
                }
            }
            else if (TryParsePort(token, out var single))
            {
                ports.Add(single);
            }
        }

        return ports.ToList();
    }

    private static bool TryParsePort(string text, out int port)
    {
        return int.TryParse(text, out port) && port is > 0 and <= 65535;
    }
}

public sealed class SwaggerScanOptions
{
    public string Host { get; set; } = "localhost";

    public IReadOnlyList<int>? Ports { get; set; }

    public IReadOnlyList<string>? Paths { get; set; }

    public IReadOnlyList<string>? Schemes { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    public int MaxConcurrency { get; set; } = 16;
}

public sealed class SwaggerScanResult
{
    public required string Url { get; init; }

    public required string Host { get; init; }

    public required int Port { get; init; }

    public required string Scheme { get; init; }

    public required string Path { get; init; }

    public string? Title { get; init; }

    public string? Version { get; init; }

    public int PathCount { get; init; }

    public int OperationCount { get; init; }

    public required string SuggestedName { get; init; }
}
