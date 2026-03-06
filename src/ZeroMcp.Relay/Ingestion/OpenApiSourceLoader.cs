using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Readers.Exceptions;

namespace ZeroMcp.Relay.Ingestion;

public sealed class OpenApiSourceLoader(HttpClient httpClient)
{
    public async Task<OpenApiLoadResult> LoadAsync(string source, CancellationToken cancellationToken = default)
    {
        var content = await LoadSourceContentAsync(source, cancellationToken);
        var reader = new OpenApiStringReader();
        OpenApiDocument? document;
        OpenApiDiagnostic diagnostic;
        try
        {
            document = reader.Read(content, out diagnostic);
        }
        catch (OpenApiUnsupportedSpecVersionException)
        {
            // Microsoft.OpenApi.Readers currently has limited 3.1 support.
            // For v1 relay behavior, we fall back to 3.0 parsing semantics.
            var normalized = content.Replace("\"openapi\": \"3.1.0\"", "\"openapi\": \"3.0.3\"", StringComparison.OrdinalIgnoreCase)
                .Replace("openapi: 3.1.0", "openapi: 3.0.3", StringComparison.OrdinalIgnoreCase);
            document = reader.Read(normalized, out diagnostic);
        }

        var result = new OpenApiLoadResult
        {
            Document = document ?? new OpenApiDocument(),
            Source = source,
            LoadedAtUtc = DateTimeOffset.UtcNow
        };

        foreach (var error in diagnostic.Errors)
        {
            result.Errors.Add(error.Message);
        }

        foreach (var warning in diagnostic.Warnings)
        {
            result.Warnings.Add(warning.Message);
        }

        return result;
    }

    private async Task<string> LoadSourceContentAsync(string source, CancellationToken cancellationToken)
    {
        if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var fileUri = new Uri(source, UriKind.Absolute);
            return await File.ReadAllTextAsync(fileUri.LocalPath, cancellationToken);
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            return await httpClient.GetStringAsync(uri, cancellationToken);
        }

        throw new InvalidOperationException($"Unsupported OpenAPI source '{source}'. Expected HTTP(S) or file:// URL.");
    }
}
