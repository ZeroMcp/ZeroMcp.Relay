using ZeroMcp.Relay.Config;

namespace ZeroMcp.Relay.Tests;

public sealed class ConfigPhaseTests
{
    [Fact]
    public void GlobMatcher_IncludeThenExclude_ReturnsExpected()
    {
        var include = new[] { "stripe_*" };
        var exclude = new[] { "stripe_*_test_*" };

        Assert.True(GlobMatcher.IsIncluded("stripe_charge_create", include, exclude));
        Assert.False(GlobMatcher.IsIncluded("stripe_charge_test_run", include, exclude));
        Assert.False(GlobMatcher.IsIncluded("crm_get_customer", include, exclude));
    }

    [Fact]
    public void ConfigValidation_DetectsDuplicatePrefixes()
    {
        var resolver = new EnvironmentSecretResolver();
        var service = new RelayConfigService(resolver);

        var config = new RelayConfig
        {
            Apis =
            [
                new ApiConfig { Name = "stripe", Source = "https://example.test/stripe", Prefix = "shared" },
                new ApiConfig { Name = "crm", Source = "https://example.test/crm", Prefix = "shared" }
            ]
        };

        var validation = service.Validate(config);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "duplicate_prefix");
    }
}
