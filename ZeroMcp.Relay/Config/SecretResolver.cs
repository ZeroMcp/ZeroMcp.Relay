namespace ZeroMcp.Relay.Config;

public interface ISecretResolver
{
    SecretResolveResult Resolve(string? value);
}

public sealed record SecretResolveResult(bool IsResolved, string? Value, string? Error);

public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public SecretResolveResult Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new SecretResolveResult(true, value, null);
        }

        if (!value.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            return new SecretResolveResult(true, value, null);
        }

        var variableName = value["env:".Length..];
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return new SecretResolveResult(false, null, "Environment variable reference is empty.");
        }

        var resolved = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return new SecretResolveResult(false, null, $"Environment variable '{variableName}' is not set.");
        }

        return new SecretResolveResult(true, resolved, null);
    }
}
