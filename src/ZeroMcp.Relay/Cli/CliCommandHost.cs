using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ZeroMcp.Relay.Config;
using ZeroMcp.Relay.Ingestion;
using ZeroMcp.Relay.Server;

namespace ZeroMcp.Relay.Cli;

public sealed class CliCommandHost(IServiceProvider serviceProvider)
{
    private readonly RelayConfigService _configService = serviceProvider.GetRequiredService<RelayConfigService>();
    private readonly OpenApiSourceLoader _loader = serviceProvider.GetRequiredService<OpenApiSourceLoader>();
    private readonly ISecretResolver _secretResolver = serviceProvider.GetRequiredService<ISecretResolver>();
    private readonly RelayRuntime _runtime = serviceProvider.GetRequiredService<RelayRuntime>();

    public async Task<int> RunConfigureAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintConfigureHelp();
            return 1;
        }

        var sub = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());
        return sub switch
        {
            "init" => await ConfigureInitAsync(options),
            "add" => await ConfigureAddAsync(options),
            "remove" => await ConfigureRemoveAsync(options),
            "list" => await ConfigureListAsync(options),
            "show" => await ConfigureShowAsync(options),
            "enable" => await ConfigureToggleAsync(options, true),
            "disable" => await ConfigureToggleAsync(options, false),
            "test" => await ConfigureTestAsync(options),
            "set-secret" => await ConfigureSetSecretAsync(options),
            _ => 1
        };
    }

    public async Task<int> RunToolsAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintToolsHelp();
            return 1;
        }

        var sub = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());
        var configPath = GetSingle(options, "--config");
        await _runtime.InitializeAsync(configPath, validateOnStart: false, lazy: false, failFast: false);

        return sub switch
        {
            "list" => await ToolsListAsync(options),
            "inspect" => await ToolsInspectAsync(options),
            "count" => ToolsCount(),
            _ => 1
        };
    }

    public async Task<int> RunValidateAsync(string[] args)
    {
        var options = ParseOptions(args);
        var strict = options.ContainsKey("--strict");
        var configPath = GetSingle(options, "--config");
        var config = await _configService.LoadAsync(configPath);
        var configValidation = _configService.Validate(config);
        var issues = new List<ConfigValidationIssue>(configValidation.Issues);
        var generationWarnings = new List<ToolGenerationWarning>();

        foreach (var api in config.Apis.Where(api => api.Enabled))
        {
            try
            {
                var load = await _loader.LoadAsync(api.Source);
                if (load.Errors.Count > 0)
                {
                    foreach (var error in load.Errors)
                    {
                        issues.Add(new ConfigValidationIssue(ValidationSeverity.Error, "spec_load_error", error, api.Name));
                    }

                    continue;
                }

                var generator = serviceProvider.GetRequiredService<OpenApiToolGenerator>();
                var generated = generator.Generate(api, load.Document);
                generationWarnings.AddRange(generated.Warnings);
            }
            catch (Exception ex)
            {
                issues.Add(new ConfigValidationIssue(ValidationSeverity.Error, "spec_load_exception", ex.Message, api.Name));
            }
        }

        foreach (var warning in generationWarnings.OrderBy(w => w.ApiName).ThenBy(w => w.Code))
        {
            var severity = strict ? ValidationSeverity.Error : ValidationSeverity.Warning;
            issues.Add(new ConfigValidationIssue(severity, warning.Code, warning.Message, warning.ApiName));
        }

        foreach (var issue in issues.OrderBy(i => i.ApiName).ThenBy(i => i.Code))
        {
            var scope = string.IsNullOrWhiteSpace(issue.ApiName) ? "global" : issue.ApiName;
            Console.WriteLine($"{issue.Severity.ToString().ToUpperInvariant(),-7} [{scope}] {issue.Code}: {issue.Message}");
        }

        return issues.Any(i => i.Severity == ValidationSeverity.Error) ? 1 : 0;
    }

    private async Task<int> ConfigureInitAsync(Dictionary<string, List<string>> options)
    {
        var path = _configService.ResolveConfigPath(GetSingle(options, "--config"));
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"Config already exists at '{path}'.");
            return 1;
        }

        await _configService.SaveAsync(_configService.CreateDefault(), path);
        Console.WriteLine($"Created {path}");
        return 0;
    }

    private async Task<int> ConfigureAddAsync(Dictionary<string, List<string>> options)
    {
        var name = RequireOption(options, "-n", "--name");
        var source = RequireOption(options, "-s", "--source");
        var configPath = GetSingle(options, "--config");
        var config = await _configService.LoadAsync(configPath);
        if (config.Apis.Any(api => api.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"API '{name}' already exists.");
            return 1;
        }

        var timeout = TryParseInt(GetSingle(options, "--timeout"));
        var api = new ApiConfig
        {
            Name = name,
            Source = source,
            Prefix = GetSingle(options, "--prefix") ?? name,
            Enabled = !options.ContainsKey("--disabled"),
            Timeout = timeout > 0 ? timeout : null,
            Headers = ParseHeaders(options.GetValueOrDefault("-h") ?? options.GetValueOrDefault("--header") ?? []),
            Auth = ParseAuth(options)
        };

        config.Apis.Add(api);
        await _configService.SaveAsync(config, configPath);
        Console.WriteLine($"Added API '{name}'.");
        return 0;
    }

    private async Task<int> ConfigureRemoveAsync(Dictionary<string, List<string>> options)
    {
        var name = RequireOption(options, "-n", "--name");
        var configPath = GetSingle(options, "--config");
        var config = await _configService.LoadAsync(configPath);
        var removed = config.Apis.RemoveAll(api => api.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            Console.Error.WriteLine($"API '{name}' was not found.");
            return 1;
        }

        await _configService.SaveAsync(config, configPath);
        Console.WriteLine($"Removed API '{name}'.");
        return 0;
    }

    private async Task<int> ConfigureListAsync(Dictionary<string, List<string>> options)
    {
        var config = await _configService.LoadAsync(GetSingle(options, "--config"));
        Console.WriteLine("NAME       AUTH         ENABLED SOURCE");
        foreach (var api in config.Apis.OrderBy(api => api.Name, StringComparer.OrdinalIgnoreCase))
        {
            var auth = api.Auth?.Type ?? "none";
            Console.WriteLine($"{api.Name,-10} {auth,-12} {(api.Enabled ? "yes" : "no"),-7} {api.Source}");
        }

        return 0;
    }

    private async Task<int> ConfigureShowAsync(Dictionary<string, List<string>> options)
    {
        var name = RequireOption(options, "-n", "--name");
        var config = await _configService.LoadAsync(GetSingle(options, "--config"));
        var api = config.Apis.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (api is null)
        {
            Console.Error.WriteLine($"API '{name}' was not found.");
            return 1;
        }

        var masked = ConfigMasking.CreateMaskedCopy(new RelayConfig
        {
            Apis = [api],
            DefaultTimeout = config.DefaultTimeout,
            ServerName = config.ServerName,
            ServerVersion = config.ServerVersion,
            Schema = config.Schema
        });

        Console.WriteLine(JsonSerializer.Serialize(masked.Apis[0], new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private async Task<int> ConfigureToggleAsync(Dictionary<string, List<string>> options, bool enabled)
    {
        var name = RequireOption(options, "-n", "--name");
        var configPath = GetSingle(options, "--config");
        var config = await _configService.LoadAsync(configPath);
        var api = config.Apis.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (api is null)
        {
            Console.Error.WriteLine($"API '{name}' was not found.");
            return 1;
        }

        api.Enabled = enabled;
        await _configService.SaveAsync(config, configPath);
        Console.WriteLine($"{(enabled ? "Enabled" : "Disabled")} API '{name}'.");
        return 0;
    }

    private async Task<int> ConfigureTestAsync(Dictionary<string, List<string>> options)
    {
        var name = RequireOption(options, "-n", "--name");
        var config = await _configService.LoadAsync(GetSingle(options, "--config"));
        var api = config.Apis.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (api is null)
        {
            Console.Error.WriteLine($"API '{name}' was not found.");
            return 1;
        }

        try
        {
            var load = await _loader.LoadAsync(api.Source);
            if (load.Errors.Count > 0)
            {
                Console.Error.WriteLine($"Spec parse failed: {string.Join("; ", load.Errors)}");
                return 1;
            }

            var secretError = ValidateAuthSecrets(api);
            if (!string.IsNullOrWhiteSpace(secretError))
            {
                Console.Error.WriteLine(secretError);
                return 1;
            }

            var baseUrl = !string.IsNullOrWhiteSpace(api.BaseUrl) ? api.BaseUrl : load.Document.Servers.FirstOrDefault()?.Url;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                Console.Error.WriteLine("No base URL was found.");
                return 1;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(api.Timeout ?? config.DefaultTimeout) };
            var request = new HttpRequestMessage(HttpMethod.Options, baseUrl);
            var response = await client.SendAsync(request);
            Console.WriteLine($"Connection test succeeded with status {(int)response.StatusCode}.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connection test failed: {ex.Message}");
            return 1;
        }
    }

    private async Task<int> ConfigureSetSecretAsync(Dictionary<string, List<string>> options)
    {
        var name = RequireOption(options, "-n", "--name");
        var configPath = GetSingle(options, "--config");
        var config = await _configService.LoadAsync(configPath);
        var api = config.Apis.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (api is null)
        {
            Console.Error.WriteLine($"API '{name}' was not found.");
            return 1;
        }

        api.Auth ??= new AuthConfig { Type = "none" };
        if (TryGetOption(options, "--bearer", out var bearer))
        {
            api.Auth.Type = "bearer";
            api.Auth.Token = bearer;
        }
        else if (TryGetOption(options, "--api-key", out var apiKey))
        {
            api.Auth.Type = "apikey";
            api.Auth.Value = apiKey;
            api.Auth.Header ??= "X-Api-Key";
        }
        else if (TryGetOption(options, "--password", out var password))
        {
            api.Auth.Type = "basic";
            api.Auth.Password = password;
        }
        else
        {
            Console.Error.WriteLine("Expected one of: --bearer, --api-key, --password.");
            return 1;
        }

        await _configService.SaveAsync(config, configPath);
        Console.WriteLine($"Updated secret for API '{name}'.");
        return 0;
    }

    private async Task<int> ToolsListAsync(Dictionary<string, List<string>> options)
    {
        await _runtime.EnsureApisLoadedAsync(validateOnStart: false, failFast: false);
        var apiFilter = GetSingle(options, "-n") ?? GetSingle(options, "--name");
        var format = (GetSingle(options, "--format") ?? "table").ToLowerInvariant();
        var tools = _runtime.Tools
            .Where(tool => string.IsNullOrWhiteSpace(apiFilter) || tool.ApiName.Equals(apiFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(tools, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine("NAME                           API        METHOD PATH");
        foreach (var tool in tools)
        {
            Console.WriteLine($"{tool.Name,-30} {tool.ApiName,-10} {tool.HttpMethod,-6} {tool.Path}");
        }

        return 0;
    }

    private async Task<int> ToolsInspectAsync(Dictionary<string, List<string>> options)
    {
        var toolName = RequireOption(options, "-t", "--tool-name");
        await _runtime.EnsureApisLoadedAsync(validateOnStart: false, failFast: false);
        var tool = _runtime.Tools.FirstOrDefault(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
        {
            Console.Error.WriteLine($"Tool '{toolName}' was not found.");
            return 1;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            tool.Name,
            tool.Description,
            tool.ApiName,
            tool.HttpMethod,
            tool.Path,
            tool.InputSchema
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    private int ToolsCount()
    {
        var grouped = _runtime.Tools.GroupBy(t => t.ApiName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { ApiName = g.Key, Count = g.Count() })
            .OrderBy(g => g.ApiName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Total tools: {_runtime.Tools.Count}");
        foreach (var entry in grouped)
        {
            Console.WriteLine($"  {entry.ApiName}: {entry.Count}");
        }

        return 0;
    }

    private static Dictionary<string, string> ParseHeaders(IEnumerable<string> values)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var split = value.Split('=', 2, StringSplitOptions.TrimEntries);
            if (split.Length == 2 && !string.IsNullOrWhiteSpace(split[0]))
            {
                headers[split[0]] = split[1];
            }
        }

        return headers;
    }

    private AuthConfig? ParseAuth(Dictionary<string, List<string>> options)
    {
        if (TryGetOption(options, "-b", out var bearer) || TryGetOption(options, "--bearer", out bearer))
        {
            return new AuthConfig { Type = "bearer", Token = bearer };
        }

        if (TryGetOption(options, "-k", out var apiKey) || TryGetOption(options, "--api-key", out apiKey))
        {
            return new AuthConfig { Type = "apikey", Header = "X-Api-Key", Value = apiKey };
        }

        var hasUser = TryGetOption(options, "-u", out var user) || TryGetOption(options, "--username", out user);
        var hasPassword = TryGetOption(options, "-p", out var password) || TryGetOption(options, "--password", out password);
        if (hasUser || hasPassword)
        {
            return new AuthConfig { Type = "basic", Username = user, Password = password };
        }

        return new AuthConfig { Type = "none" };
    }

    private string? ValidateAuthSecrets(ApiConfig api)
    {
        if (api.Auth is null)
        {
            return null;
        }

        var candidates = new[] { api.Auth.Token, api.Auth.Value, api.Auth.Password };
        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var resolved = _secretResolver.Resolve(candidate);
            if (!resolved.IsResolved)
            {
                return resolved.Error;
            }
        }

        return null;
    }

    private static Dictionary<string, List<string>> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (!options.TryGetValue(token, out var values))
            {
                values = [];
                options[token] = values;
            }

            if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                values.Add(args[i + 1]);
                i++;
            }
        }

        return options;
    }

    private static string? GetSingle(Dictionary<string, List<string>> options, string key)
    {
        return options.TryGetValue(key, out var values) && values.Count > 0 ? values[^1] : null;
    }

    private static bool TryGetOption(Dictionary<string, List<string>> options, string key, out string value)
    {
        value = string.Empty;
        var found = GetSingle(options, key);
        if (string.IsNullOrWhiteSpace(found))
        {
            return false;
        }

        value = found;
        return true;
    }

    private static string RequireOption(Dictionary<string, List<string>> options, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = GetSingle(options, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        throw new InvalidOperationException($"Missing required option: {string.Join(" or ", keys)}");
    }

    private static int TryParseInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }

    private static void PrintConfigureHelp()
    {
        Console.WriteLine("Usage: mcprelay configure <init|add|remove|list|show|enable|disable|test|set-secret>");
    }

    private static void PrintToolsHelp()
    {
        Console.WriteLine("Usage: mcprelay tools <list|inspect|count>");
    }
}
