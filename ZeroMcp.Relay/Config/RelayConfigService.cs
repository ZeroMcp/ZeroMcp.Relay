using System.Text.Json;

namespace ZeroMcp.Relay.Config;

public sealed class RelayConfigService(ISecretResolver secretResolver)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string ResolveConfigPath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var localPath = Path.Combine(Environment.CurrentDirectory, "relay.config.json");
        if (File.Exists(localPath))
        {
            return localPath;
        }

        var homeConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".mcprelay",
            "config.json");

        return homeConfigPath;
    }

    public async Task<RelayConfig> LoadAsync(string? configuredPath = null, CancellationToken cancellationToken = default)
    {
        var path = ResolveConfigPath(configuredPath);
        if (!File.Exists(path))
        {
            return new RelayConfig();
        }

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<RelayConfig>(stream, JsonOptions, cancellationToken);
        return config ?? new RelayConfig();
    }

    public async Task SaveAsync(RelayConfig config, string? configuredPath = null, CancellationToken cancellationToken = default)
    {
        var path = ResolveConfigPath(configuredPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken);
        }

        var retries = 5;
        while (true)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);
                break;
            }
            catch (UnauthorizedAccessException) when (retries > 0)
            {
                retries--;
                await Task.Delay(50, cancellationToken);
            }
            catch (IOException) when (retries > 0)
            {
                retries--;
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    public RelayConfig CreateDefault()
    {
        return new RelayConfig
        {
            Apis = []
        };
    }

    public ConfigValidationResult Validate(RelayConfig config)
    {
        var result = new ConfigValidationResult();

        if (config.DefaultTimeout <= 0)
        {
            result.AddError("default_timeout_invalid", "defaultTimeout must be greater than zero.");
        }

        var duplicateApiNames = config.Apis
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        foreach (var name in duplicateApiNames)
        {
            result.AddError("duplicate_api_name", $"Duplicate API name '{name}' found.");
        }

        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var api in config.Apis)
        {
            ValidateApi(api, config.DefaultTimeout, prefixes, result);
        }

        return result;
    }

    private void ValidateApi(ApiConfig api, int defaultTimeout, HashSet<string> prefixes, ConfigValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(api.Name))
        {
            result.AddError("api_name_missing", "API name is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(api.Source))
        {
            result.AddError("api_source_missing", $"API '{api.Name}' source is required.", api.Name);
        }

        var effectivePrefix = string.IsNullOrWhiteSpace(api.Prefix) ? api.Name : api.Prefix;
        if (!prefixes.Add(effectivePrefix))
        {
            result.AddError("duplicate_prefix", $"API prefix '{effectivePrefix}' must be unique.", api.Name);
        }

        var timeout = api.Timeout ?? defaultTimeout;
        if (timeout <= 0)
        {
            result.AddError("api_timeout_invalid", $"API '{api.Name}' timeout must be greater than zero.", api.Name);
        }

        ValidateAuth(api, result);
    }

    private void ValidateAuth(ApiConfig api, ConfigValidationResult result)
    {
        if (api.Auth is null)
        {
            return;
        }

        var type = api.Auth.Type.Trim().ToLowerInvariant();
        switch (type)
        {
            case "none":
            case "passthrough":
                return;
            case "bearer":
                ValidateSecret(api.Auth.Token, api.Name, "bearer_token_missing", "Bearer token is required.", result);
                return;
            case "apikey":
                if (string.IsNullOrWhiteSpace(api.Auth.Header))
                {
                    result.AddError("api_key_header_missing", "API key header name is required.", api.Name);
                }

                ValidateSecret(api.Auth.Value, api.Name, "api_key_value_missing", "API key value is required.", result);
                return;
            case "apikey-query":
                if (string.IsNullOrWhiteSpace(api.Auth.Parameter))
                {
                    result.AddError("api_key_parameter_missing", "API key query parameter is required.", api.Name);
                }

                ValidateSecret(api.Auth.Value, api.Name, "api_key_value_missing", "API key value is required.", result);
                return;
            case "basic":
                if (string.IsNullOrWhiteSpace(api.Auth.Username))
                {
                    result.AddError("basic_username_missing", "Basic auth username is required.", api.Name);
                }

                ValidateSecret(api.Auth.Password, api.Name, "basic_password_missing", "Basic auth password is required.", result);
                return;
            default:
                result.AddError("auth_type_invalid", $"Unsupported auth type '{api.Auth.Type}'.", api.Name);
                return;
        }
    }

    private void ValidateSecret(string? secretRef, string apiName, string code, string missingMessage, ConfigValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
        {
            result.AddError(code, missingMessage, apiName);
            return;
        }

        var resolved = secretResolver.Resolve(secretRef);
        if (!resolved.IsResolved)
        {
            result.AddWarning("secret_unresolved", resolved.Error ?? "Secret could not be resolved.", apiName);
        }
    }

    public (bool IsValid, string? Error) ValidateApiSecrets(ApiConfig api)
    {
        if (api.Auth is null)
        {
            return (true, null);
        }

        var authType = api.Auth.Type.Trim().ToLowerInvariant();
        if (authType is "none" or "passthrough")
        {
            return (true, null);
        }

        var requiredSecrets = authType switch
        {
            "bearer" => new[] { api.Auth.Token },
            "apikey" => new[] { api.Auth.Value },
            "apikey-query" => new[] { api.Auth.Value },
            "basic" => new[] { api.Auth.Password },
            _ => Array.Empty<string?>()
        };

        foreach (var secret in requiredSecrets.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var resolved = secretResolver.Resolve(secret);
            if (!resolved.IsResolved)
            {
                return (false, resolved.Error ?? "Secret could not be resolved.");
            }
        }

        return (true, null);
    }
}
