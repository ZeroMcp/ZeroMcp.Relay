using ZeroMcp.Relay.Config;
using ZeroMcp.Relay.Server;

namespace ZeroMcp.Relay.Tests;

public sealed class PhaseSixSevenTests
{
    [Fact]
    public void ConfigMasking_MasksAuthSecrets()
    {
        var config = new RelayConfig
        {
            Apis =
            [
                new ApiConfig
                {
                    Name = "stripe",
                    Source = "https://example.test/spec.json",
                    Auth = new AuthConfig
                    {
                        Type = "bearer",
                        Token = "sk_live_secret_value"
                    }
                }
            ]
        };

        var masked = ConfigMasking.CreateMaskedCopy(config);
        Assert.Equal("sk_l****", masked.Apis[0].Auth?.Token);
    }

    [Fact]
    public void RunOptionsParser_ParsesEnvPath()
    {
        var options = RunOptionsParser.Parse(["--env", ".env.production"]);
        Assert.Equal(".env.production", options.EnvPath);
    }
}
