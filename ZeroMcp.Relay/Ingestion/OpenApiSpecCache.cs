using ZeroMcp.Relay.Config;

namespace ZeroMcp.Relay.Ingestion;

public sealed class OpenApiSpecCache(OpenApiSourceLoader loader)
{
    private readonly Dictionary<string, OpenApiLoadResult> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<OpenApiLoadResult> GetOrLoadAsync(ApiConfig api, bool forceReload = false, CancellationToken cancellationToken = default)
    {
        if (!forceReload && _cache.TryGetValue(api.Name, out var existing))
        {
            return existing;
        }

        var loaded = await loader.LoadAsync(api.Source, cancellationToken);
        _cache[api.Name] = loaded;
        return loaded;
    }

    public bool TryGet(string apiName, out OpenApiLoadResult? result)
    {
        var found = _cache.TryGetValue(apiName, out var cached);
        result = cached;
        return found;
    }

    public IReadOnlyDictionary<string, OpenApiLoadResult> GetAll() => _cache;
}
