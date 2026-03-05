using ZeroMcp.Relay.Server;

namespace ZeroMcp.Relay.Tests;

public sealed class ServerPhaseTests
{
    [Fact]
    public void RunOptionsParser_ParsesHttpAndStdioFlags()
    {
        var options = RunOptionsParser.Parse(
        [
            "--host", "0.0.0.0",
            "--port", "8080",
            "--config", "relay.config.json",
            "--stdio",
            "--enable-ui",
            "--lazy"
        ]);

        Assert.Equal("0.0.0.0", options.Host);
        Assert.Equal(8080, options.Port);
        Assert.Equal("relay.config.json", options.ConfigPath);
        Assert.True(options.Stdio);
        Assert.True(options.EnableUi);
        Assert.True(options.Lazy);
    }
}
