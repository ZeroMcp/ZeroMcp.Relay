using Microsoft.OpenApi.Models;
using ZeroMcp.Relay.Config;
using ZeroMcp.Relay.Ingestion;
using ZeroMcp.Relay.Relay;

namespace ZeroMcp.Relay.Server;

public sealed class RelayRuntime(
    RelayConfigService configService,
    OpenApiSpecCache specCache,
    OpenApiToolGenerator toolGenerator,
    RelayDispatcher dispatcher)
{
    private readonly Dictionary<string, LoadedApiState> _apiStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolBinding> _toolBindings = new(StringComparer.OrdinalIgnoreCase);
    private RelayConfig _config = new();

    public RelayConfig Config => _config;

    public IReadOnlyDictionary<string, LoadedApiState> ApiStates => _apiStates;

    public IReadOnlyCollection<ToolDefinition> Tools => _toolBindings.Values.Select(binding => binding.Tool).ToList();

    public async Task InitializeAsync(string? configPath, bool validateOnStart, bool lazy, bool failFast, CancellationToken cancellationToken = default)
    {
        _config = await configService.LoadAsync(configPath, cancellationToken);
        var validation = configService.Validate(_config);
        if (!validation.IsValid)
        {
            var message = string.Join("; ", validation.Issues
                .Where(issue => issue.Severity == ValidationSeverity.Error)
                .Select(issue => issue.Message));
            throw new InvalidOperationException($"Configuration validation failed: {message}");
        }

        _apiStates.Clear();
        _toolBindings.Clear();

        if (lazy)
        {
            foreach (var api in _config.Apis.Where(api => api.Enabled))
            {
                _apiStates[api.Name] = new LoadedApiState
                {
                    Api = api,
                    Document = new OpenApiDocument(),
                    Tools = [],
                    Status = "not_loaded"
                };
            }

            return;
        }

        await EnsureApisLoadedAsync(validateOnStart, failFast, cancellationToken);
    }

    public Task ReloadAsync(string? configPath, bool validateOnStart, bool lazy, bool failFast, CancellationToken cancellationToken = default)
        => InitializeAsync(configPath, validateOnStart, lazy, failFast, cancellationToken);

    public async Task EnsureApisLoadedAsync(bool validateOnStart, bool failFast, CancellationToken cancellationToken = default)
    {
        foreach (var api in _config.Apis.Where(a => a.Enabled))
        {
            if (_apiStates.TryGetValue(api.Name, out var existing) && existing.Status == "ok")
            {
                continue;
            }

            try
            {
                var loaded = await specCache.GetOrLoadAsync(api, false, cancellationToken);
                if (!loaded.IsSuccess && validateOnStart)
                {
                    throw new InvalidOperationException(string.Join("; ", loaded.Errors));
                }

                var generated = toolGenerator.Generate(api, loaded.Document);
                var state = new LoadedApiState
                {
                    Api = api,
                    Document = loaded.Document,
                    Tools = generated.Tools,
                    LoadedAtUtc = loaded.LoadedAtUtc,
                    Status = loaded.IsSuccess ? "ok" : "degraded",
                    Error = loaded.IsSuccess ? null : string.Join("; ", loaded.Errors)
                };

                _apiStates[api.Name] = state;
                IndexToolBindings(state);
            }
            catch (Exception ex)
            {
                _apiStates[api.Name] = new LoadedApiState
                {
                    Api = api,
                    Document = new OpenApiDocument(),
                    Tools = [],
                    Status = "error",
                    Error = ex.Message
                };

                if (failFast)
                {
                    throw;
                }
            }
        }
    }

    public async Task EnsureApiLoadedAsync(string apiName, CancellationToken cancellationToken = default)
    {
        if (!_apiStates.TryGetValue(apiName, out var state))
        {
            return;
        }

        if (state.Status == "ok" && state.Tools.Count > 0)
        {
            return;
        }

        var api = state.Api;
        var loaded = await specCache.GetOrLoadAsync(api, false, cancellationToken);
        if (!loaded.IsSuccess)
        {
            state.Status = "error";
            state.Error = string.Join("; ", loaded.Errors);
            return;
        }

        var generated = toolGenerator.Generate(api, loaded.Document);
        state.Document = loaded.Document;
        state.Tools = generated.Tools;
        state.LoadedAtUtc = loaded.LoadedAtUtc;
        state.Status = "ok";
        state.Error = null;
        IndexToolBindings(state);
    }

    public async Task<RelayDispatchResult> DispatchAsync(string toolName, System.Text.Json.JsonElement? args, CancellationToken cancellationToken = default)
    {
        if (!_toolBindings.TryGetValue(toolName, out var binding))
        {
            return new RelayDispatchResult
            {
                IsError = true,
                Content = $"Unknown tool '{toolName}'."
            };
        }

        await EnsureApiLoadedAsync(binding.Api.Name, cancellationToken);

        var operation = FindOperation(binding.Document, binding.Tool);
        if (operation is null)
        {
            return new RelayDispatchResult
            {
                IsError = true,
                Content = $"Operation '{binding.Tool.OperationId}' not found for tool '{toolName}'."
            };
        }

        var baseUrl = ResolveBaseUrl(binding.Api, binding.Document);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new RelayDispatchResult
            {
                IsError = true,
                Content = $"Unable to resolve base URL for API '{binding.Api.Name}'."
            };
        }

        var timeout = binding.Api.Timeout ?? _config.DefaultTimeout;
        return await dispatcher.DispatchAsync(binding.Api, binding.Tool, operation, baseUrl, args, timeout, cancellationToken);
    }

    public object BuildHealthResponse()
    {
        var apis = _apiStates.Values
            .Select(state => new
            {
                name = state.Api.Name,
                status = state.Status,
                toolCount = state.Tools.Count,
                error = state.Error,
                specAge = state.LoadedAtUtc == default ? null : (DateTimeOffset.UtcNow - state.LoadedAtUtc).ToString("c")
            })
            .ToList();

        var status = apis.Count == 0 || apis.All(api => api.status == "error")
            ? "error"
            : apis.Any(api => api.status == "error" || api.status == "degraded")
                ? "degraded"
                : "ok";

        return new
        {
            status,
            apis,
            totalTools = _toolBindings.Count
        };
    }

    private void IndexToolBindings(LoadedApiState state)
    {
        foreach (var tool in state.Tools)
        {
            _toolBindings[tool.Name] = new ToolBinding
            {
                Tool = tool,
                Api = state.Api,
                Document = state.Document
            };
        }
    }

    private static OpenApiOperation? FindOperation(OpenApiDocument document, ToolDefinition tool)
    {
        if (!document.Paths.TryGetValue(tool.Path, out var pathItem))
        {
            return null;
        }

        if (!Enum.TryParse<OperationType>(tool.HttpMethod, true, out var operationType))
        {
            return null;
        }

        if (!pathItem.Operations.TryGetValue(operationType, out var operation))
        {
            return null;
        }

        return operation;
    }

    private static string? ResolveBaseUrl(ApiConfig api, OpenApiDocument document)
    {
        if (!string.IsNullOrWhiteSpace(api.BaseUrl))
        {
            return api.BaseUrl;
        }

        var server = document.Servers.FirstOrDefault();
        return server?.Url;
    }
}
