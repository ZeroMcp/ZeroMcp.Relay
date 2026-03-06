namespace ZeroMcp.Relay.Config;

public static class SecretMasker
{
    public static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        const int visiblePrefix = 4;
        if (value.Length <= visiblePrefix)
        {
            return "****";
        }

        return $"{value[..visiblePrefix]}****";
    }
}
