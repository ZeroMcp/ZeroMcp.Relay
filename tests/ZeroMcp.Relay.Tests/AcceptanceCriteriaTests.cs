using System.Net;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ZeroMcp.Relay.Cli;
using ZeroMcp.Relay.Config;
using ZeroMcp.Relay.Ingestion;
using ZeroMcp.Relay.Relay;
using ZeroMcp.Relay.Server;

namespace ZeroMcp.Relay.Tests;

[Collection("SerialTests")]
public sealed class AcceptanceCriteriaTests : IDisposable
{
    private readonly string _tempRoot;

    public AcceptanceCriteriaTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ZeroMcpRelayTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Cli_InstallMetadata_Exists()
    {
        var csprojPath = Path.Combine(FindRepoRoot(), "ZeroMcp.Relay", "ZeroMcp.Relay.csproj");
        var content = File.ReadAllText(csprojPath);
        Assert.Contains("<PackAsTool>true</PackAsTool>", content);
        Assert.Contains("<ToolCommandName>mcprelay</ToolCommandName>", content);
    }

    [Fact]
    public async Task Cli_ConfigureInit_CreatesValidConfig()
    {
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        var configPath = Path.Combine(_tempRoot, "relay.config.json");
        var code = await host.RunConfigureAsync(["init", "--config", configPath]);
        Assert.Equal(0, code);
        var json = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
        Assert.True(json.RootElement.TryGetProperty("apis", out _));
    }

    [Fact]
    public async Task Cli_ConfigureAdd_AllAuthTypes_WriteCorrectConfig()
    {
        var configPath = await CreateBaseConfigAsync();
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        var specPath = await CreateOpenApiThreeFileAsync();

        Assert.Equal(0, await host.RunConfigureAsync(["add", "-n", "bear", "-s", $"file://{specPath}", "-b", "env:BEAR", "--config", configPath]));
        Assert.Equal(0, await host.RunConfigureAsync(["add", "-n", "key", "-s", $"file://{specPath}", "-k", "env:APIK", "--config", configPath]));
        Assert.Equal(0, await host.RunConfigureAsync(["add", "-n", "basic", "-s", $"file://{specPath}", "-u", "u", "-p", "env:PW", "--config", configPath]));

        var service = provider.GetRequiredService<RelayConfigService>();
        var loaded = await service.LoadAsync(configPath);
        Assert.Contains(loaded.Apis, a => a.Name == "bear" && a.Auth?.Type == "bearer");
        Assert.Contains(loaded.Apis, a => a.Name == "key" && a.Auth?.Type == "apikey");
        Assert.Contains(loaded.Apis, a => a.Name == "basic" && a.Auth?.Type == "basic");
    }

    [Fact]
    public async Task Cli_ConfigureList_ReportsStatusAndToolCount()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        var text = await CaptureStdoutAsync(() => host.RunConfigureAsync(["list", "--config", configPath]));
        Assert.Contains("TOOLS", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("yes", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_ConfigureTest_ValidatesSpecAndAuthResolution()
    {
        Environment.SetEnvironmentVariable("TEST_TOKEN", "token123");
        var listener = await StartListenerAsync(_ => Task.CompletedTask, HttpStatusCode.OK);
        var specPath = await CreateOpenApiThreeFileAsync(serverUrl: listener.BaseUrl);
        var configPath = await CreateBaseConfigAsync();
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        Assert.Equal(0, await host.RunConfigureAsync(["add", "-n", "svc", "-s", $"file://{specPath}", "-b", "env:TEST_TOKEN", "--config", configPath]));
        Assert.Equal(0, await host.RunConfigureAsync(["test", "-n", "svc", "--config", configPath]));
        listener.Dispose();
    }

    [Fact]
    public async Task Cli_ConfigureRemove_Yes_RemovesEntry()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        Assert.Equal(0, await host.RunConfigureAsync(["remove", "-n", "api1", "--yes", "--config", configPath]));
        var service = provider.GetRequiredService<RelayConfigService>();
        var loaded = await service.LoadAsync(configPath);
        Assert.Empty(loaded.Apis);
    }

    [Fact]
    public async Task Cli_ConfigureRemove_PromptsForConfirmation()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();

        var oldIn = Console.In;
        try
        {
            Console.SetIn(new StringReader("n" + Environment.NewLine));
            var code = await host.RunConfigureAsync(["remove", "-n", "api1", "--config", configPath]);
            Assert.Equal(1, code);
            var loaded = await provider.GetRequiredService<RelayConfigService>().LoadAsync(configPath);
            Assert.Single(loaded.Apis);
        }
        finally
        {
            Console.SetIn(oldIn);
        }
    }

    [Fact]
    public async Task Cli_ConfigureEnableDisable_TogglesWithoutRemoving()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        Assert.Equal(0, await host.RunConfigureAsync(["disable", "-n", "api1", "--config", configPath]));
        Assert.Equal(0, await host.RunConfigureAsync(["enable", "-n", "api1", "--config", configPath]));
        var loaded = await provider.GetRequiredService<RelayConfigService>().LoadAsync(configPath);
        Assert.Single(loaded.Apis);
        Assert.True(loaded.Apis[0].Enabled);
    }

    [Fact]
    public async Task Cli_ToolsList_ShowsAllEnabledTools()
    {
        var configPath = await CreateConfigWithTwoApisAsync();
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        var output = await CaptureStdoutAsync(() => host.RunToolsAsync(["list", "--config", configPath]));
        Assert.Contains("api1_get_echo", output);
        Assert.Contains("api2_get_echo", output);
    }

    [Fact]
    public async Task Cli_ToolsInspect_ShowsSchema()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        var output = await CaptureStdoutAsync(() => host.RunToolsAsync(["inspect", "-t", "api1_get_echo", "--config", configPath]));
        Assert.Contains("InputSchema", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_Validate_ExitCodeBehavior()
    {
        var validPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        var validCode = await host.RunValidateAsync(["--config", validPath]);
        Assert.Equal(0, validCode);

        var invalidPath = Path.Combine(_tempRoot, "invalid.config.json");
        await File.WriteAllTextAsync(invalidPath, """{"defaultTimeout":0,"apis":[{"name":"x","source":""}]}""");
        var invalidCode = await host.RunValidateAsync(["--config", invalidPath]);
        Assert.Equal(1, invalidCode);
    }

    [Fact]
    public async Task Cli_ValidateStrict_TreatsWarningsAsErrors()
    {
        var missingOpSpec = await CreateOpenApiMissingOperationIdFileAsync();
        var configPath = await CreateBaseConfigAsync();
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        Assert.Equal(0, await host.RunConfigureAsync(["add", "-n", "warnapi", "-s", $"file://{missingOpSpec}", "--config", configPath]));
        Assert.Equal(1, await host.RunValidateAsync(["--strict", "--config", configPath]));
    }

    [Fact]
    public void Http_DefaultRunOptions_BindLocalhost5000()
    {
        var options = RunOptionsParser.Parse([]);
        Assert.Equal("localhost", options.Host);
        Assert.Equal(5000, options.Port);
    }

    [Fact]
    public async Task Http_McpRouter_HandlesInitializeToolsListToolsCall()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var router = provider.GetRequiredService<McpRouter>();

        using var initialize = JsonDocument.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        var initResponse = await router.HandleAsync(initialize.RootElement);
        Assert.Contains("serverName", JsonSerializer.Serialize(initResponse));

        using var list = JsonDocument.Parse("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var listResponse = await router.HandleAsync(list.RootElement);
        Assert.Contains("tools", JsonSerializer.Serialize(listResponse));

        using var call = JsonDocument.Parse("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"unknown_tool","arguments":{}}}""");
        var callResponse = await router.HandleAsync(call.RootElement);
        Assert.Contains("\"isError\":true", JsonSerializer.Serialize(callResponse));
    }

    [Fact]
    public async Task Http_McpTools_MergesTools()
    {
        var configPath = await CreateConfigWithTwoApisAsync();
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        Assert.True(runtime.Tools.Count >= 2);
    }

    [Fact]
    public async Task Http_Health_ReturnsApiStatusAndTotal()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var json = JsonSerializer.Serialize(runtime.BuildHealthResponse());
        Assert.Contains("totalTools", json);
        Assert.Contains("apis", json);
    }

    [Fact]
    public async Task Http_Dispatch_Maps4xx5xxToIsError()
    {
        var listener = await StartListenerAsync(_ => Task.CompletedTask, HttpStatusCode.InternalServerError, "boom");
        var configPath = await CreateConfigWithOneApiAsync(enabled: true, serverUrl: listener.BaseUrl);
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var result = await runtime.DispatchAsync("api1_get_echo", null);
        Assert.True(result.IsError);
        listener.Dispose();
    }

    [Fact]
    public async Task Http_Dispatch_ConstructsUrlAndAuthHeadersCorrectly()
    {
        var observed = new RequestObservation();
        var listener = await StartListenerAsync(context =>
        {
            observed.Path = context.Request.Url?.AbsolutePath ?? string.Empty;
            observed.Query = context.Request.Url?.Query ?? string.Empty;
            observed.Authorization = context.Request.Headers["Authorization"];
            return Task.CompletedTask;
        }, HttpStatusCode.OK, "ok");

        Environment.SetEnvironmentVariable("HTTP_AUTH_TOKEN", "abc-token");
        var specPath = await CreateOpenApiParamSpecAsync(listener.BaseUrl);
        var configPath = Path.Combine(_tempRoot, "dispatch-auth.config.json");
        var cfg = new RelayConfig
        {
            Apis =
            [
                new ApiConfig
                {
                    Name = "api1",
                    Source = $"file://{specPath}",
                    Prefix = "api1",
                    Enabled = true,
                    Auth = new AuthConfig { Type = "bearer", Token = "env:HTTP_AUTH_TOKEN" }
                }
            ]
        };

        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(cfg, configPath);
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);

        using var argsDoc = JsonDocument.Parse("""{"id":"42","page":"2"}""");
        var result = await runtime.DispatchAsync("api1_get_items_id", argsDoc.RootElement);
        Assert.False(result.IsError);
        Assert.Equal("/items/42", observed.Path);
        Assert.Contains("page=2", observed.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Bearer abc-token", observed.Authorization);
        listener.Dispose();
    }

    [Fact]
    public async Task Http_Dispatch_RespectsTimeouts()
    {
        var listener = await StartListenerAsync(async _ => await Task.Delay(2000), HttpStatusCode.OK);
        var configPath = await CreateConfigWithOneApiAsync(enabled: true, serverUrl: listener.BaseUrl, timeoutSeconds: 1);
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var result = await runtime.DispatchAsync("api1_get_echo", null);
        Assert.True(result.IsError);
        Assert.Contains("timed out", result.Content, StringComparison.OrdinalIgnoreCase);
        listener.Dispose();
    }

    [Fact]
    public async Task Http_DisabledApis_ExcludedFromToolsList()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: false);
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        Assert.Empty(runtime.Tools);
    }

    [Fact]
    public async Task Http_SpecFailure_DegradesWithRemainingApis()
    {
        var validSpec = await CreateOpenApiThreeFileAsync();
        var badSpec = "file:///no/such/spec.json";
        var configPath = Path.Combine(_tempRoot, "degraded.config.json");
        var config = new RelayConfig
        {
            Apis =
            [
                new ApiConfig { Name = "good", Source = $"file://{validSpec}", Prefix = "good", Enabled = true },
                new ApiConfig { Name = "bad", Source = badSpec, Prefix = "bad", Enabled = true }
            ]
        };

        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(config, configPath);
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var health = JsonSerializer.Serialize(runtime.BuildHealthResponse());
        Assert.Contains("\"degraded\"", health, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ui_Routes_NotRegisteredWithoutEnableUi()
    {
        var routes = HttpServer.GetRegisteredRouteTemplates(enableUi: false);
        Assert.DoesNotContain("/ui", routes);
    }

    [Fact]
    public void Ui_Routes_RegisteredWithEnableUi()
    {
        var routes = HttpServer.GetRegisteredRouteTemplates(enableUi: true);
        Assert.Contains("/ui", routes);
        Assert.Contains("/admin/reload", routes);
    }

    [Fact]
    public async Task Ui_AddFlow_WritesConfig()
    {
        var configPath = await CreateBaseConfigAsync();
        var specPath = await CreateOpenApiThreeFileAsync();
        using var provider = BuildProvider();
        var host = provider.GetRequiredService<CliCommandHost>();
        Assert.Equal(0, await host.RunConfigureAsync(["add", "-n", "uiapi", "-s", $"file://{specPath}", "--config", configPath]));
        var loaded = await provider.GetRequiredService<RelayConfigService>().LoadAsync(configPath);
        Assert.Contains(loaded.Apis, a => a.Name == "uiapi");
    }

    [Fact]
    public async Task Ui_StatusIndicators_MapToApiState()
    {
        var validSpec = await CreateOpenApiThreeFileAsync();
        var config = new RelayConfig
        {
            Apis =
            [
                new ApiConfig { Name = "okapi", Source = $"file://{validSpec}", Prefix = "okapi", Enabled = true },
                new ApiConfig { Name = "disabledapi", Source = $"file://{validSpec}", Prefix = "disabledapi", Enabled = false },
                new ApiConfig { Name = "errorapi", Source = "file:///missing.json", Prefix = "errorapi", Enabled = true }
            ]
        };
        var configPath = Path.Combine(_tempRoot, "status.config.json");
        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(config, configPath);
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        Assert.Contains(runtime.ApiStates.Values, s => s.Status == "ok");
        Assert.Contains(runtime.ApiStates.Values, s => s.Status == "disabled");
        Assert.Contains(runtime.ApiStates.Values, s => s.Status == "error");
    }

    [Fact]
    public async Task Ui_TestConnection_ReportsStatusAndCount()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        await runtime.EnsureApiLoadedAsync("api1");
        var state = runtime.ApiStates["api1"];
        Assert.Equal("ok", state.Status);
        Assert.True(state.Tools.Count > 0);
    }

    [Fact]
    public async Task Ui_ToolBrowser_CanFilterSelectedApi()
    {
        var configPath = await CreateConfigWithTwoApisAsync();
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var api1Tools = runtime.Tools.Where(t => t.ApiName.Equals("api1", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(api1Tools);
        Assert.All(api1Tools, t => Assert.StartsWith("api1_", t.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ui_ToolInspector_ShowsFullInputSchema()
    {
        var specPath = await CreateOpenApiBodySpecAsync();
        var configPath = Path.Combine(_tempRoot, "ui-inspect.config.json");
        var cfg = new RelayConfig
        {
            Apis =
            [
                new ApiConfig { Name = "api1", Source = $"file://{specPath}", Prefix = "api1", Enabled = true, Auth = new AuthConfig { Type = "none" } }
            ]
        };

        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(cfg, configPath);
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var tool = runtime.Tools.First(t => t.Name == "api1_post_orders");
        var schema = JsonSerializer.Serialize(tool.InputSchema);
        Assert.Contains("customerId", schema);
    }

    [Fact]
    public async Task Ui_ToolInvoke_ReturnsRawResponse()
    {
        var listener = await StartListenerAsync(_ => Task.CompletedTask, HttpStatusCode.OK, "raw-response-body");
        var configPath = await CreateConfigWithOneApiAsync(enabled: true, serverUrl: listener.BaseUrl);
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var result = await runtime.DispatchAsync("api1_get_echo", null);
        Assert.Equal("raw-response-body", result.Content);
        listener.Dispose();
    }

    [Fact]
    public async Task Ui_RawConfig_MasksSecrets()
    {
        var specPath = await CreateOpenApiThreeFileAsync();
        var cfg = new RelayConfig
        {
            Apis =
            [
                new ApiConfig
                {
                    Name = "masked",
                    Source = $"file://{specPath}",
                    Prefix = "masked",
                    Enabled = true,
                    Auth = new AuthConfig { Type = "bearer", Token = "super-secret-token" }
                }
            ]
        };

        var masked = ConfigMasking.CreateMaskedCopy(cfg);
        Assert.NotEqual("super-secret-token", masked.Apis[0].Auth?.Token);
        Assert.Contains('*', masked.Apis[0].Auth?.Token ?? string.Empty);
    }

    [Fact]
    public async Task Stdio_ReadsStdinAndWritesStdout()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var router = provider.GetRequiredService<McpRouter>();
        var server = new StdioServer(router);

        var oldIn = Console.In;
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        try
        {
            var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""" + Environment.NewLine);
            var output = new StringWriter();
            var error = new StringWriter();
            Console.SetIn(input);
            Console.SetOut(output);
            Console.SetError(error);
            await server.RunAsync();
            Assert.Contains("\"jsonrpc\":\"2.0\"", output.ToString());
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public async Task Stdio_LogsToStderr()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        using var provider = BuildProvider();
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var router = provider.GetRequiredService<McpRouter>();
        var server = new StdioServer(router);

        var oldIn = Console.In;
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        try
        {
            var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"api1_get_echo","arguments":{}}}""" + Environment.NewLine);
            var output = new StringWriter();
            var error = new StringWriter();
            Console.SetIn(input);
            Console.SetOut(output);
            Console.SetError(error);
            await server.RunAsync();
            Assert.Contains("correlation=", error.ToString());
        }
        finally
        {
            Console.SetIn(oldIn);
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    [Fact]
    public async Task Stdio_EnableUiFlag_EmitsIgnoredWarning()
    {
        var configPath = await CreateConfigWithOneApiAsync(enabled: true);
        var result = await RunToolProcessAsync(["run", "--stdio", "--enable-ui", "--config", configPath], """{"jsonrpc":"2.0","id":1,"method":"initialize"}""" + Environment.NewLine);
        Assert.Contains("ignored", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stdio_SpecLoadFailure_WritesJsonRpcError_AndExitsNonZero()
    {
        var configPath = Path.Combine(_tempRoot, "stdio-fail.config.json");
        var cfg = new RelayConfig
        {
            Apis = [new ApiConfig { Name = "bad", Source = "file:///missing/spec.json", Prefix = "bad", Enabled = true }]
        };

        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(cfg, configPath);

        var result = await RunToolProcessAsync(["run", "--stdio", "--config", configPath]);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("\"jsonrpc\":\"2.0\"", result.Stdout);
        Assert.Contains("\"error\":", result.Stdout);
    }

    [Fact]
    public void Stdio_ClaudeDesktopExampleArgs_AreParseable()
    {
        var options = RunOptionsParser.Parse(["--stdio", "--config", "/path/to/project/relay.config.json"]);
        Assert.True(options.Stdio);
        Assert.Equal("/path/to/project/relay.config.json", options.ConfigPath);
    }

    [Fact]
    public async Task OpenApi_Loads30_31_20_JsonYaml()
    {
        using var provider = BuildProvider();
        var loader = provider.GetRequiredService<OpenApiSourceLoader>();
        var v30 = await CreateOpenApiThreeFileAsync();
        var v31 = await CreateOpenApiThreeOneFileAsync();
        var v20 = await CreateSwaggerTwoFileAsync();
        var yaml = await CreateOpenApiYamlFileAsync();

        Assert.True((await loader.LoadAsync($"file://{v30}")).IsSuccess);
        Assert.True((await loader.LoadAsync($"file://{v31}")).IsSuccess);
        Assert.True((await loader.LoadAsync($"file://{v20}")).IsSuccess);
        Assert.True((await loader.LoadAsync($"file://{yaml}")).IsSuccess);
    }

    [Fact]
    public void OpenApi_RefChainsResolved()
    {
        var doc = new Microsoft.OpenApi.Models.OpenApiDocument
        {
            Components = new Microsoft.OpenApi.Models.OpenApiComponents
            {
                Schemas = new Dictionary<string, Microsoft.OpenApi.Models.OpenApiSchema>
                {
                    ["Outer"] = new() { Type = "object", Properties = new Dictionary<string, Microsoft.OpenApi.Models.OpenApiSchema> { ["inner"] = new() { Reference = new Microsoft.OpenApi.Models.OpenApiReference { Id = "Inner", Type = Microsoft.OpenApi.Models.ReferenceType.Schema } } } },
                    ["Inner"] = new() { Type = "object", Properties = new Dictionary<string, Microsoft.OpenApi.Models.OpenApiSchema> { ["value"] = new() { Type = "string" } } }
                }
            },
            Paths = new Microsoft.OpenApi.Models.OpenApiPaths
            {
                ["/x"] = new Microsoft.OpenApi.Models.OpenApiPathItem
                {
                    Operations = new Dictionary<Microsoft.OpenApi.Models.OperationType, Microsoft.OpenApi.Models.OpenApiOperation>
                    {
                        [Microsoft.OpenApi.Models.OperationType.Post] = new Microsoft.OpenApi.Models.OpenApiOperation
                        {
                            OperationId = "RefOp",
                            RequestBody = new Microsoft.OpenApi.Models.OpenApiRequestBody
                            {
                                Content = new Dictionary<string, Microsoft.OpenApi.Models.OpenApiMediaType>
                                {
                                    ["application/json"] = new() { Schema = new Microsoft.OpenApi.Models.OpenApiSchema { Reference = new Microsoft.OpenApi.Models.OpenApiReference { Id = "Outer", Type = Microsoft.OpenApi.Models.ReferenceType.Schema } } }
                                }
                            }
                        }
                    }
                }
            }
        };

        var generated = new OpenApiToolGenerator().Generate(new ApiConfig { Name = "r", Prefix = "r" }, doc);
        var schemaText = JsonSerializer.Serialize(generated.Tools[0].InputSchema);
        Assert.Contains("value", schemaText);
    }

    [Fact]
    public void OpenApi_MissingOperationId_GeneratesWarning()
    {
        var doc = new Microsoft.OpenApi.Models.OpenApiDocument
        {
            Paths = new Microsoft.OpenApi.Models.OpenApiPaths
            {
                ["/x"] = new Microsoft.OpenApi.Models.OpenApiPathItem
                {
                    Operations = new Dictionary<Microsoft.OpenApi.Models.OperationType, Microsoft.OpenApi.Models.OpenApiOperation>
                    {
                        [Microsoft.OpenApi.Models.OperationType.Get] = new()
                    }
                }
            }
        };
        var generated = new OpenApiToolGenerator().Generate(new ApiConfig { Name = "a", Prefix = "a" }, doc);
        Assert.Contains(generated.Warnings, w => w.Code == "missing_operation_id");
    }

    [Fact]
    public void OpenApi_IncludeExcludeFilter_Works()
    {
        var doc = new Microsoft.OpenApi.Models.OpenApiDocument
        {
            Paths = new Microsoft.OpenApi.Models.OpenApiPaths
            {
                ["/x"] = new Microsoft.OpenApi.Models.OpenApiPathItem
                {
                    Operations = new Dictionary<Microsoft.OpenApi.Models.OperationType, Microsoft.OpenApi.Models.OpenApiOperation>
                    {
                        [Microsoft.OpenApi.Models.OperationType.Get] = new() { OperationId = "GetX" },
                        [Microsoft.OpenApi.Models.OperationType.Post] = new() { OperationId = "PostX" }
                    }
                }
            }
        };
        var api = new ApiConfig { Name = "a", Prefix = "a", Include = ["a_get_*"], Exclude = ["*post*"] };
        var generated = new OpenApiToolGenerator().Generate(api, doc);
        Assert.Single(generated.Tools);
        Assert.Equal("a_get_x", generated.Tools[0].Name);
    }

    [Fact]
    public void OpenApi_DuplicatePrefix_ShowsValidationError()
    {
        var service = BuildProvider().GetRequiredService<RelayConfigService>();
        var cfg = new RelayConfig
        {
            Apis =
            [
                new ApiConfig { Name = "a", Source = "file://a", Prefix = "dup" },
                new ApiConfig { Name = "b", Source = "file://b", Prefix = "dup" }
            ]
        };
        var result = service.Validate(cfg);
        Assert.Contains(result.Issues, i => i.Code == "duplicate_prefix");
    }

    [Fact]
    public async Task OpenApi_DuplicatePrefix_StartFailsClearly()
    {
        var specPath = await CreateOpenApiThreeFileAsync();
        var configPath = Path.Combine(_tempRoot, "duplicate-prefix.config.json");
        var cfg = new RelayConfig
        {
            Apis =
            [
                new ApiConfig { Name = "a", Source = $"file://{specPath}", Prefix = "dup", Enabled = true },
                new ApiConfig { Name = "b", Source = $"file://{specPath}", Prefix = "dup", Enabled = true }
            ]
        };
        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(cfg, configPath);
        var runtime = provider.GetRequiredService<RelayRuntime>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.InitializeAsync(configPath, true, false, true));
        Assert.Contains("Configuration validation failed", ex.Message);
    }

    [Fact]
    public void SecretHandling_EnvReferencesResolve()
    {
        Environment.SetEnvironmentVariable("AC_SECRET", "value1");
        var resolver = new EnvironmentSecretResolver();
        var result = resolver.Resolve("env:AC_SECRET");
        Assert.True(result.IsResolved);
        Assert.Equal("value1", result.Value);
    }

    [Fact]
    public async Task SecretHandling_MissingEnv_DisablesApiWithoutCrash()
    {
        Environment.SetEnvironmentVariable("MISSING_AC", null);
        var specPath = await CreateOpenApiThreeFileAsync();
        var config = new RelayConfig
        {
            Apis =
            [
                new ApiConfig
                {
                    Name = "s",
                    Source = $"file://{specPath}",
                    Prefix = "s",
                    Enabled = true,
                    Auth = new AuthConfig { Type = "bearer", Token = "env:MISSING_AC" }
                }
            ]
        };
        var configPath = Path.Combine(_tempRoot, "secretfail.config.json");
        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(config, configPath);
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, failFast: false);
        Assert.Equal("error", runtime.ApiStates["s"].Status);
    }

    [Fact]
    public void SecretHandling_MissingEnv_ValidationWarns()
    {
        Environment.SetEnvironmentVariable("WARN_MISSING_SECRET", null);
        using var provider = BuildProvider();
        var service = provider.GetRequiredService<RelayConfigService>();
        var cfg = new RelayConfig
        {
            Apis =
            [
                new ApiConfig
                {
                    Name = "warn",
                    Source = "file://spec",
                    Prefix = "warn",
                    Enabled = true,
                    Auth = new AuthConfig { Type = "bearer", Token = "env:WARN_MISSING_SECRET" }
                }
            ]
        };

        var result = service.Validate(cfg);
        Assert.Contains(result.Issues, i => i.Code == "secret_unresolved");
    }

    [Fact]
    public async Task SecretHandling_EnvFileLoadsBeforeResolution()
    {
        var envPath = Path.Combine(_tempRoot, ".env.test");
        await File.WriteAllTextAsync(envPath, "AC_ENV_FROM_FILE=abc123");
        EnvFileLoader.Load(envPath);
        var resolver = new EnvironmentSecretResolver();
        var resolved = resolver.Resolve("env:AC_ENV_FROM_FILE");
        Assert.True(resolved.IsResolved);
        Assert.Equal("abc123", resolved.Value);
    }

    [Fact]
    public async Task SecretHandling_NoSecretsInHealthPayload()
    {
        var specPath = await CreateOpenApiThreeFileAsync();
        var config = new RelayConfig
        {
            Apis =
            [
                new ApiConfig
                {
                    Name = "s",
                    Source = $"file://{specPath}",
                    Prefix = "s",
                    Enabled = true,
                    Auth = new AuthConfig { Type = "bearer", Token = "env:TOPSECRET" }
                }
            ]
        };
        var configPath = Path.Combine(_tempRoot, "healthsecret.config.json");
        Environment.SetEnvironmentVariable("TOPSECRET", "plainsecret");
        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(config, configPath);
        var runtime = provider.GetRequiredService<RelayRuntime>();
        await runtime.InitializeAsync(configPath, true, false, false);
        var health = JsonSerializer.Serialize(runtime.BuildHealthResponse());
        Assert.DoesNotContain("plainsecret", health);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch
        {
            // no-op
        }
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecretResolver, EnvironmentSecretResolver>();
        services.AddSingleton<RelayConfigService>();
        services.AddSingleton(sp => new OpenApiSourceLoader(new HttpClient()));
        services.AddSingleton<OpenApiSpecCache>();
        services.AddSingleton<OpenApiToolGenerator>();
        services.AddSingleton<RelayDispatcher>();
        services.AddSingleton<RelayRuntime>();
        services.AddSingleton<McpRouter>();
        services.AddSingleton<StdioServer>();
        services.AddSingleton<HttpServer>();
        services.AddSingleton<CliCommandHost>();
        return services.BuildServiceProvider();
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ZeroMcp.Relay.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private async Task<string> CreateBaseConfigAsync()
    {
        var path = Path.Combine(_tempRoot, "relay.config.json");
        var cfg = new RelayConfig();
        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(cfg, path);
        return path;
    }

    private async Task<string> CreateConfigWithOneApiAsync(bool enabled, string? serverUrl = null, int timeoutSeconds = 30)
    {
        var specPath = await CreateOpenApiThreeFileAsync(serverUrl);
        var path = Path.Combine(_tempRoot, $"oneapi-{Guid.NewGuid():n}.config.json");
        var cfg = new RelayConfig
        {
            Apis =
            [
                new ApiConfig
                {
                    Name = "api1",
                    Source = $"file://{specPath}",
                    Prefix = "api1",
                    Enabled = enabled,
                    Timeout = timeoutSeconds,
                    Auth = new AuthConfig { Type = "none" }
                }
            ]
        };
        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(cfg, path);
        return path;
    }

    private async Task<string> CreateConfigWithTwoApisAsync()
    {
        var spec1 = await CreateOpenApiThreeFileAsync();
        var spec2 = await CreateOpenApiThreeFileAsync();
        var path = Path.Combine(_tempRoot, $"twoapi-{Guid.NewGuid():n}.config.json");
        var cfg = new RelayConfig
        {
            Apis =
            [
                new ApiConfig { Name = "api1", Source = $"file://{spec1}", Prefix = "api1", Enabled = true, Auth = new AuthConfig { Type = "none" } },
                new ApiConfig { Name = "api2", Source = $"file://{spec2}", Prefix = "api2", Enabled = true, Auth = new AuthConfig { Type = "none" } }
            ]
        };
        using var provider = BuildProvider();
        await provider.GetRequiredService<RelayConfigService>().SaveAsync(cfg, path);
        return path;
    }

    private async Task<string> CreateOpenApiThreeFileAsync(string? serverUrl = null)
    {
        var path = Path.Combine(_tempRoot, $"spec-{Guid.NewGuid():n}.json");
        var url = serverUrl ?? "https://example.test";
        var json = $$"""
{
  "openapi": "3.0.3",
  "info": { "title": "Test API", "version": "1.0.0" },
  "servers": [{ "url": "{{url}}" }],
  "paths": {
    "/echo": {
      "get": {
        "operationId": "GetEcho",
        "summary": "Echo endpoint",
        "responses": { "200": { "description": "ok" } }
      }
    }
  }
}
""";
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private async Task<string> CreateOpenApiMissingOperationIdFileAsync()
    {
        var path = Path.Combine(_tempRoot, $"missing-op-{Guid.NewGuid():n}.json");
        var json = """
{
  "openapi": "3.0.3",
  "info": { "title": "Warn API", "version": "1.0.0" },
  "servers": [{ "url": "https://example.test" }],
  "paths": {
    "/warn": {
      "get": {
        "responses": { "200": { "description": "ok" } }
      }
    }
  }
}
""";
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private async Task<string> CreateOpenApiThreeOneFileAsync()
    {
        var path = Path.Combine(_tempRoot, $"spec31-{Guid.NewGuid():n}.json");
        var json = """
{
  "openapi": "3.1.0",
  "info": { "title": "Test 31", "version": "1.0.0" },
  "servers": [{ "url": "https://example.test" }],
  "paths": { "/x": { "get": { "operationId": "GetX", "responses": { "200": { "description": "ok" } } } } }
}
""";
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private async Task<string> CreateOpenApiParamSpecAsync(string serverUrl)
    {
        var path = Path.Combine(_tempRoot, $"paramspec-{Guid.NewGuid():n}.json");
        var json = $$"""
{
  "openapi": "3.0.3",
  "info": { "title": "Param API", "version": "1.0.0" },
  "servers": [{ "url": "{{serverUrl}}" }],
  "paths": {
    "/items/{id}": {
      "get": {
        "operationId": "GetItemsId",
        "parameters": [
          { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
          { "name": "page", "in": "query", "required": false, "schema": { "type": "string" } }
        ],
        "responses": { "200": { "description": "ok" } }
      }
    }
  }
}
""";
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private async Task<string> CreateOpenApiBodySpecAsync()
    {
        var path = Path.Combine(_tempRoot, $"bodyspec-{Guid.NewGuid():n}.json");
        var json = """
{
  "openapi": "3.0.3",
  "info": { "title": "Body API", "version": "1.0.0" },
  "servers": [{ "url": "https://example.test" }],
  "paths": {
    "/orders": {
      "post": {
        "operationId": "PostOrders",
        "requestBody": {
          "required": true,
          "content": {
            "application/json": {
              "schema": {
                "type": "object",
                "properties": {
                  "customerId": { "type": "string" },
                  "amount": { "type": "number" }
                }
              }
            }
          }
        },
        "responses": { "200": { "description": "ok" } }
      }
    }
  }
}
""";
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private async Task<string> CreateSwaggerTwoFileAsync()
    {
        var path = Path.Combine(_tempRoot, $"swagger2-{Guid.NewGuid():n}.json");
        var json = """
{
  "swagger": "2.0",
  "info": { "title": "Swagger2", "version": "1.0.0" },
  "host": "example.test",
  "schemes": ["https"],
  "paths": {
    "/x": {
      "get": {
        "operationId": "GetX",
        "responses": { "200": { "description": "ok" } }
      }
    }
  }
}
""";
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private async Task<string> CreateOpenApiYamlFileAsync()
    {
        var path = Path.Combine(_tempRoot, $"yaml-{Guid.NewGuid():n}.yaml");
        var yaml = """
openapi: 3.0.3
info:
  title: Yaml API
  version: 1.0.0
servers:
  - url: https://example.test
paths:
  /y:
    get:
      operationId: GetY
      responses:
        '200':
          description: ok
""";
        await File.WriteAllTextAsync(path, yaml);
        return path;
    }

    private async Task<string> CaptureStdoutAsync(Func<Task<int>> action)
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        var writer = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(writer);
        Console.SetError(error);
        try
        {
            _ = await action();
            return writer + Environment.NewLine + error;
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private async Task<TestListener> StartListenerAsync(Func<HttpListenerContext, Task> onRequest, HttpStatusCode statusCode, string responseBody = "ok")
    {
        var port = GetFreePort();
        var prefix = $"http://localhost:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    await onRequest(context);
                    context.Response.StatusCode = (int)statusCode;
                    var payload = Encoding.UTF8.GetBytes(responseBody);
                    await context.Response.OutputStream.WriteAsync(payload);
                    context.Response.Close();
                }
                catch
                {
                    break;
                }
            }
        });

        return new TestListener(listener, prefix.TrimEnd('/'));
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record TestListener(HttpListener Listener, string BaseUrl) : IDisposable
    {
        public void Dispose()
        {
            if (Listener.IsListening)
            {
                Listener.Stop();
            }

            Listener.Close();
        }
    }

    private static async Task<ProcessResult> RunToolProcessAsync(string[] args, string? stdin = null)
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "ZeroMcp.Relay", "ZeroMcp.Relay.csproj");
        var escapedArgs = string.Join(" ", args.Select(EscapeShellArg));
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --framework net10.0 --project \"{projectPath}\" -- {escapedArgs}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        if (!string.IsNullOrEmpty(stdin))
        {
            await process.StandardInput.WriteAsync(stdin);
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string EscapeShellArg(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return "\"\"";
        }

        if (!arg.Contains(' ') && !arg.Contains('"'))
        {
            return arg;
        }

        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    private sealed record RequestObservation
    {
        public string Path { get; set; } = string.Empty;
        public string Query { get; set; } = string.Empty;
        public string? Authorization { get; set; }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
