using System.Text;
using System.Text.Json;
using Microsoft.OpenApi.Models;
using ZeroMcp.Relay.Config;
using ZeroMcp.Relay.Ingestion;

namespace ZeroMcp.Relay.Relay;

public sealed class RelayDispatcher(ISecretResolver secretResolver)
{
    public async Task<RelayDispatchResult> DispatchAsync(
        ApiConfig api,
        ToolDefinition tool,
        OpenApiOperation operation,
        string baseUrl,
        JsonElement? arguments,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string[]>? inboundHeaders = null,
        CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        var requestUri = BuildRequestUri(baseUrl, tool.Path, operation, arguments);
        using var request = new HttpRequestMessage(new HttpMethod(tool.HttpMethod), requestUri);

        ApplyHeaders(api, operation, arguments, request);
        ApplyBody(operation, arguments, request);

        var authSecret = ResolveAuthSecret(api);
        request.RequestUri = ApiAuthApplicator.ApplyAuth(api, request, requestUri, authSecret, inboundHeaders);
        ApplyForwardedHeaders(api, request, inboundHeaders);

        try
        {
            var response = await client.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            return new RelayDispatchResult
            {
                IsError = !response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                Content = payload
            };
        }
        catch (TaskCanceledException)
        {
            return new RelayDispatchResult
            {
                IsError = true,
                Content = $"Request timed out after {timeoutSeconds}s."
            };
        }
    }

    private static Uri BuildRequestUri(string baseUrl, string path, OpenApiOperation operation, JsonElement? arguments)
    {
        var expandedPath = path;
        if (arguments is JsonElement argsElement && argsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var parameter in operation.Parameters.Where(p => p.In == ParameterLocation.Path))
            {
                if (argsElement.TryGetProperty(parameter.Name, out var value))
                {
                    expandedPath = expandedPath.Replace("{" + parameter.Name + "}", Uri.EscapeDataString(value.ToString()), StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        var uriBuilder = new StringBuilder();
        uriBuilder.Append(baseUrl.TrimEnd('/'));
        uriBuilder.Append('/');
        uriBuilder.Append(expandedPath.TrimStart('/'));

        var queryParts = new List<string>();
        if (arguments is JsonElement queryArgs && queryArgs.ValueKind == JsonValueKind.Object)
        {
            foreach (var parameter in operation.Parameters.Where(p => p.In == ParameterLocation.Query))
            {
                if (queryArgs.TryGetProperty(parameter.Name, out var value))
                {
                    queryParts.Add($"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(value.ToString())}");
                }
            }
        }

        if (queryParts.Count > 0)
        {
            uriBuilder.Append('?');
            uriBuilder.Append(string.Join("&", queryParts));
        }

        return new Uri(uriBuilder.ToString(), UriKind.Absolute);
    }

    private static void ApplyHeaders(ApiConfig api, OpenApiOperation operation, JsonElement? arguments, HttpRequestMessage request)
    {
        foreach (var header in api.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (arguments is not JsonElement argsElement || argsElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var parameter in operation.Parameters.Where(p => p.In == ParameterLocation.Header))
        {
            if (argsElement.TryGetProperty(parameter.Name, out var value))
            {
                request.Headers.TryAddWithoutValidation(parameter.Name, value.ToString());
            }
        }
    }

    private static void ApplyBody(OpenApiOperation operation, JsonElement? arguments, HttpRequestMessage request)
    {
        if (operation.RequestBody is null || arguments is not JsonElement argsElement || argsElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in argsElement.EnumerateObject())
        {
            properties[property.Name] = property.Value;
        }

        var bodyObject = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var bodySchema = operation.RequestBody.Content
            .Where(content => content.Key.Contains("json", StringComparison.OrdinalIgnoreCase))
            .Select(content => content.Value.Schema)
            .FirstOrDefault();

        if (bodySchema?.Type?.Equals("object", StringComparison.OrdinalIgnoreCase) == true || bodySchema?.Properties.Count > 0)
        {
            foreach (var name in bodySchema.Properties.Keys)
            {
                if (properties.TryGetValue(name, out var value))
                {
                    bodyObject[name] = JsonSerializer.Deserialize<object>(value.GetRawText());
                }
            }
        }
        else if (properties.TryGetValue("body", out var rawBody))
        {
            request.Content = new StringContent(rawBody.GetRawText(), Encoding.UTF8, "application/json");
            return;
        }

        request.Content = new StringContent(JsonSerializer.Serialize(bodyObject), Encoding.UTF8, "application/json");
    }

    private static void ApplyForwardedHeaders(ApiConfig api, HttpRequestMessage request, IReadOnlyDictionary<string, string[]>? inboundHeaders)
    {
        if (inboundHeaders is null || api.ForwardHeaders.Count == 0)
        {
            return;
        }

        foreach (var headerName in api.ForwardHeaders)
        {
            if (string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (inboundHeaders.TryGetValue(headerName, out var values))
            {
                request.Headers.TryAddWithoutValidation(headerName, values);
            }
        }
    }

    private string? ResolveAuthSecret(ApiConfig api)
    {
        if (api.Auth is null)
        {
            return null;
        }

        var authType = api.Auth.Type.Trim().ToLowerInvariant();
        var secretRef = authType switch
        {
            "bearer" => api.Auth.Token,
            "apikey" => api.Auth.Value,
            "apikey-query" => api.Auth.Value,
            "basic" => api.Auth.Password,
            _ => null
        };

        var resolved = secretResolver.Resolve(secretRef);
        return resolved.IsResolved ? resolved.Value : null;
    }
}
