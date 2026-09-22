using Microsoft.Extensions.Caching.Memory;

namespace PromptCopilot.Api.Sessions;

public sealed class SessionStore
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _sliding;

    public SessionStore(IMemoryCache cache, TimeSpan sliding) { _cache = cache; _sliding = sliding; }

    public Session Create()
    {
        var s = new Session(Guid.NewGuid().ToString("N"));
        _cache.Set(s.Id, s, new MemoryCacheEntryOptions { SlidingExpiration = _sliding });
        return s;
    }

    public Session? TryGet(string id) => _cache.TryGetValue(id, out Session? s) ? s : null;
}
