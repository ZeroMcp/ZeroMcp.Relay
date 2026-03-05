using System.Reflection;
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
        Console.WriteLine("Not implemented yet: configure commands.");
        return 0;
    case "run":
        return await RunCommandAsync(args.Skip(1).ToArray());
    case "tools":
        Console.WriteLine("Not implemented yet: tools commands.");
        return 0;
    case "validate":
        Console.WriteLine("Not implemented yet: validate command.");
        return 0;
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
    if (options.Stdio && options.EnableUi)
    {
        await Console.Error.WriteLineAsync("Warning: --enable-ui is ignored in --stdio mode.");
    }

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

    using var provider = services.BuildServiceProvider();
    var runtime = provider.GetRequiredService<RelayRuntime>();
    var failFast = options.Stdio;

    await runtime.InitializeAsync(
        options.ConfigPath,
        options.ValidateOnStart,
        options.Lazy,
        failFast,
        CancellationToken.None);

    if (options.Stdio)
    {
        var stdio = provider.GetRequiredService<StdioServer>();
        return await stdio.RunAsync(CancellationToken.None);
    }

    var http = provider.GetRequiredService<HttpServer>();
    return await http.RunAsync(options, CancellationToken.None);
}
