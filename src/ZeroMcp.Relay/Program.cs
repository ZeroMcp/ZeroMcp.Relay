using System.Reflection;
using ZeroMcp.Relay.Cli;
using Microsoft.Extensions.DependencyInjection;
using ZeroMcp.Relay.Config;
using ZeroMcp.Relay.Ingestion;
using ZeroMcp.Relay.Relay;
using ZeroMcp.Relay.Server;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (args[0] is "--version" or "-v")
{
    Console.WriteLine(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
    return 0;
}

var command = args[0].ToLowerInvariant();

switch (command)
{
    case "configure":
        return await ConfigureCommandAsync(args.Skip(1).ToArray());
    case "run":
        return await RunCommandAsync(args.Skip(1).ToArray());
    case "tools":
        return await ToolsCommandAsync(args.Skip(1).ToArray());
    case "validate":
        return await ValidateCommandAsync(args.Skip(1).ToArray());
    default:
        Console.Error.WriteLine($"Unknown command: {args[0]}");
        PrintHelp();
        return 1;
}

static void PrintHelp()
{
    Console.WriteLine("ZeroMcp.Relay (mcprelay)");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  mcprelay configure");
    Console.WriteLine("  mcprelay run");
    Console.WriteLine("  mcprelay tools");
    Console.WriteLine("  mcprelay validate");
    Console.WriteLine("  mcprelay --version");
}

static async Task<int> RunCommandAsync(string[] runArgs)
{
    var options = RunOptionsParser.Parse(runArgs);
    if (!string.IsNullOrWhiteSpace(options.EnvPath))
    {
        EnvFileLoader.Load(options.EnvPath);
    }

    if (options.Stdio && options.EnableUi)
    {
        await Console.Error.WriteLineAsync("Warning: --enable-ui is ignored in --stdio mode.");
    }

    using var provider = BuildServices();
    var runtime = provider.GetRequiredService<RelayRuntime>();
    var failFast = options.Stdio;

    try
    {
        await runtime.InitializeAsync(
            options.ConfigPath,
            options.ValidateOnStart,
            options.Lazy,
            failFast,
            CancellationToken.None);
    }
    catch (Exception ex) when (options.Stdio)
    {
        var jsonRpcError = System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = (object?)null,
            error = new
            {
                code = -32000,
                message = ex.Message
            }
        });
        await Console.Out.WriteLineAsync(jsonRpcError);
        return 1;
    }

    if (options.Stdio)
    {
        var stdio = provider.GetRequiredService<StdioServer>();
        return await stdio.RunAsync(CancellationToken.None);
    }

    var http = provider.GetRequiredService<HttpServer>();
    return await http.RunAsync(options, CancellationToken.None);
}

static async Task<int> ConfigureCommandAsync(string[] args)
{
    using var provider = BuildServices();
    var cli = provider.GetRequiredService<CliCommandHost>();
    try
    {
        return await cli.RunConfigureAsync(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task<int> ToolsCommandAsync(string[] args)
{
    using var provider = BuildServices();
    var cli = provider.GetRequiredService<CliCommandHost>();
    try
    {
        return await cli.RunToolsAsync(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static async Task<int> ValidateCommandAsync(string[] args)
{
    using var provider = BuildServices();
    var cli = provider.GetRequiredService<CliCommandHost>();
    try
    {
        return await cli.RunValidateAsync(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static ServiceProvider BuildServices()
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
