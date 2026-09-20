using System.Net;
using System.Text;
using ZeroMcp.Relay.Ingestion;

namespace ZeroMcp.Relay.Tests;

public sealed class SwaggerScannerTests
{
    private const string SampleSpec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Inventory API", "version": "2.1.0" },
          "paths": {
            "/items": {
              "get": { "operationId": "listItems", "summary": "List items", "responses": { "200": { "description": "ok" } } },
              "post": { "operationId": "createItem", "summary": "Create item", "responses": { "201": { "description": "created" } } }
            }
          }
        }
        """;

    [Fact]
    public void PortListParser_ParsesListsAndRanges()
    {
        var ports = PortListParser.Parse("8080, 5000-5003, 443, 5001");

        Assert.Equal(new[] { 443, 5000, 5001, 5002, 5003, 8080 }, ports.ToArray());
    }

    [Fact]
    public void PortListParser_IgnoresInvalidTokens()
    {
        var ports = PortListParser.Parse("abc, 70000, 0, -5, 22");

        Assert.Equal(new[] { 22 }, ports.ToArray());
    }

    [Fact]
    public void SuggestName_SlugifiesTitle()
    {
        Assert.Equal("inventory_api", SwaggerScanner.SuggestName("Inventory API", "localhost", 8080));
    }

    [Fact]
    public void SuggestName_FallsBackToHostAndPort()
    {
        Assert.Equal("localhost_8080", SwaggerScanner.SuggestName(null, "localhost", 8080));
        Assert.Equal("api_8080", SwaggerScanner.SuggestName("   ", "", 8080));
    }

    [Fact]
    public async Task ScanAsync_DiscoversServedSpec()
    {
        using var server = new StubHttpServer(("/swagger/v1/swagger.json", SampleSpec, "application/json"));
        var scanner = new SwaggerScanner(new OpenApiSourceLoader(new HttpClient()));

        var results = await scanner.ScanAsync(new SwaggerScanOptions
        {
            Host = "localhost",
            Ports = [server.Port],
            Schemes = ["http"],
            Timeout = TimeSpan.FromSeconds(3)
        });

        var result = Assert.Single(results);
        Assert.Equal("Inventory API", result.Title);
        Assert.Equal("2.1.0", result.Version);
        Assert.Equal(2, result.OperationCount);
        Assert.Equal(server.Port, result.Port);
        Assert.Equal("http", result.Scheme);
        Assert.Equal("inventory_api", result.SuggestedName);
        Assert.EndsWith("/swagger/v1/swagger.json", result.Url);
    }

    [Fact]
    public async Task ScanAsync_IgnoresNonSpecResponses()
    {
        using var server = new StubHttpServer(("/swagger/v1/swagger.json", "<html><body>Swagger UI</body></html>", "text/html"));
        var scanner = new SwaggerScanner(new OpenApiSourceLoader(new HttpClient()));

        var results = await scanner.ScanAsync(new SwaggerScanOptions
        {
            Host = "localhost",
            Ports = [server.Port],
            Schemes = ["http"],
            Timeout = TimeSpan.FromSeconds(3)
        });

        Assert.Empty(results);
    }

    [Fact]
    public async Task ScanAsync_ReturnsEmptyForClosedPort()
    {
        var scanner = new SwaggerScanner(new OpenApiSourceLoader(new HttpClient()));
        var closedPort = GetFreePort();

        var results = await scanner.ScanAsync(new SwaggerScanOptions
        {
            Host = "localhost",
            Ports = [closedPort],
            Schemes = ["http"],
            Timeout = TimeSpan.FromMilliseconds(500)
        });

        Assert.Empty(results);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StubHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly (string Path, string Body, string ContentType) _route;

        public int Port { get; }

        public StubHttpServer((string Path, string Body, string ContentType) route)
        {
            _route = route;
            Port = GetFreePort();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = Task.Run(ListenAsync);
        }

        private async Task ListenAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? string.Empty;
                if (string.Equals(path, _route.Path, StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = Encoding.UTF8.GetBytes(_route.Body);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = _route.ContentType;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }

                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
            _cts.Dispose();
        }
    }
}
