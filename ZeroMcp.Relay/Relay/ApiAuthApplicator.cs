using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using ZeroMcp.Relay.Config;

namespace ZeroMcp.Relay.Relay;

public static class ApiAuthApplicator
{
    public static Uri ApplyAuth(
        ApiConfig api,
        HttpRequestMessage request,
        Uri originalUri,
        string? resolvedSecret,
        IReadOnlyDictionary<string, string[]>? inboundHeaders = null)
    {
        if (api.Auth is null)
        {
            return originalUri;
        }

        var authType = api.Auth.Type.Trim().ToLowerInvariant();
        switch (authType)
        {
            case "none":
                return originalUri;

            case "passthrough":
                if (inboundHeaders is not null &&
                    inboundHeaders.TryGetValue("Authorization", out var authValues) &&
                    authValues.Length > 0 &&
                    !string.IsNullOrWhiteSpace(authValues[0]))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", authValues[0]);
                }

                return originalUri;

            case "bearer":
                if (!string.IsNullOrWhiteSpace(resolvedSecret))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resolvedSecret);
                }

                return originalUri;

            case "apikey":
                if (!string.IsNullOrWhiteSpace(api.Auth.Header) && !string.IsNullOrWhiteSpace(resolvedSecret))
                {
                    request.Headers.TryAddWithoutValidation(api.Auth.Header, resolvedSecret);
                }

                return originalUri;

            case "apikey-query":
                if (!string.IsNullOrWhiteSpace(api.Auth.Parameter) && !string.IsNullOrWhiteSpace(resolvedSecret))
                {
                    var withQuery = QueryHelpers.AddQueryString(originalUri.ToString(), api.Auth.Parameter, resolvedSecret);
                    return new Uri(withQuery, UriKind.Absolute);
                }

                return originalUri;

            case "basic":
                var username = api.Auth.Username ?? string.Empty;
                var password = resolvedSecret ?? string.Empty;
                var raw = Encoding.UTF8.GetBytes($"{username}:{password}");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
                return originalUri;

            default:
                return originalUri;
        }
    }
}
