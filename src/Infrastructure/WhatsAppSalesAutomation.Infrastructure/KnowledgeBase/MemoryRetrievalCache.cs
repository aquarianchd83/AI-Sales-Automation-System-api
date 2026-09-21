using Microsoft.Extensions.Caching.Memory;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;

namespace WhatsAppSalesAutomation.Infrastructure.KnowledgeBase;

/// <summary>
/// Process-local cache behind <see cref="IRetrievalCache"/>. Every key it is given already carries the
/// tenant (see <see cref="RetrievalCacheKeys"/>), so sharing one instance across tenants is safe:
/// isolation lives in the key, not in having separate caches.
///
/// Process-local means a second instance has its own cache and a stale answer can outlive a publish
/// on the other one until its TTL. The TTLs are short for that reason, and a shared cache (Redis)
/// would slot in behind the same interface.
/// </summary>
public sealed class MemoryRetrievalCache : IRetrievalCache
{
    private readonly IMemoryCache _cache;

    public MemoryRetrievalCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        if (_cache.TryGetValue(key, out var stored) && stored is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public void Set<T>(string key, T value, TimeSpan ttl) => _cache.Set(key, value, ttl);
}
