using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>
/// Process-wide cache of each tenant's plan features, so a <c>[RequiresFeature]</c> controller does not run two or three plan
/// queries on every single request. Holds the plan's features only - the platform admin's publish/unpublish switches are
/// applied after the cache, so flipping one takes effect at once.
///
/// Kept correct by invalidation rather than by a short expiry: <c>FeatureAccessInvalidationInterceptor</c> drops a tenant's
/// entry when its Subscription row is saved, and every entry when any Plan or PlanFeature row is saved, whichever code path
/// did it. The expiry is only the backstop for a change this process never saw (another API instance, a hand-run SQL fix).
/// </summary>
public sealed class FeatureAccessCache
{
    /// <summary>How long another instance's change (or a manual SQL edit) can go unseen here at most.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly IMemoryCache _cache;
    private CancellationTokenSource _all = new();

    public FeatureAccessCache(IMemoryCache cache) => _cache = cache;

    private static string Key(Guid tenantId) => $"feature-access:{tenantId:N}";

    public bool TryGet(Guid tenantId, out IReadOnlySet<PlatformFeature> features)
    {
        if (_cache.TryGetValue(Key(tenantId), out IReadOnlySet<PlatformFeature>? cached) && cached is not null)
        {
            features = cached;
            return true;
        }

        features = null!;
        return false;
    }

    public void Set(Guid tenantId, IReadOnlySet<PlatformFeature> planFeatures)
    {
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = 1 }
            .AddExpirationToken(new CancellationChangeToken(Volatile.Read(ref _all).Token));
        _cache.Set(Key(tenantId), planFeatures, options);
    }

    public void InvalidateTenant(Guid tenantId) => _cache.Remove(Key(tenantId));

    /// <summary>Drops every tenant's entry - for a change to a plan's features, which every tenant on that plan sees.</summary>
    public void InvalidateAll()
    {
        // Cancelled, not disposed: a Set racing with this may still be reading the old source's token, and a cancelled
        // token just makes that entry expire at once.
        Interlocked.Exchange(ref _all, new CancellationTokenSource()).Cancel();
    }
}
