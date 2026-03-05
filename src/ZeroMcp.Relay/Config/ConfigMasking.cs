namespace ZeroMcp.Relay.Config;

public static class ConfigMasking
{
    public static RelayConfig CreateMaskedCopy(RelayConfig source)
    {
        return new RelayConfig
        {
            Schema = source.Schema,
            ServerName = source.ServerName,
            ServerVersion = source.ServerVersion,
            DefaultTimeout = source.DefaultTimeout,
            Apis = source.Apis.Select(MaskApi).ToList()
        };
    }

    private static ApiConfig MaskApi(ApiConfig source)
    {
        return new ApiConfig
        {
            Name = source.Name,
            Source = source.Source,
            BaseUrl = source.BaseUrl,
            Prefix = source.Prefix,
            Enabled = source.Enabled,
            Timeout = source.Timeout,
            Headers = new Dictionary<string, string>(source.Headers, StringComparer.OrdinalIgnoreCase),
            Include = [.. source.Include],
            Exclude = [.. source.Exclude],
            Auth = source.Auth is null
                ? null
                : new AuthConfig
                {
                    Type = source.Auth.Type,
                    Header = source.Auth.Header,
                    Parameter = source.Auth.Parameter,
                    Username = source.Auth.Username,
                    Token = SecretMasker.Mask(source.Auth.Token),
                    Value = SecretMasker.Mask(source.Auth.Value),
                    Password = SecretMasker.Mask(source.Auth.Password)
                }
        };
    }
}
