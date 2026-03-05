using System.Text.Json;

namespace ZeroMcp.Relay.Server;

public sealed class StdioServer(McpRouter router)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var response = await router.HandleAsync(document.RootElement, cancellationToken);
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
                await Console.Out.FlushAsync();
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"stdio parse/dispatch error: {ex.Message}");
            }
        }

        return 0;
    }
}
