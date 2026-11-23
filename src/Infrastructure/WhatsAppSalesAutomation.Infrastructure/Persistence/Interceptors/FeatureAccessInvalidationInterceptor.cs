using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Domain.Entities.Billing;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Keeps <see cref="FeatureAccessCache"/> honest whichever code path changes a plan: a saved Subscription drops that tenant's
/// entry (plan bought, renewed, cancelled, refunded), a saved Plan or PlanFeature drops every entry (the platform admin edited
/// what a plan includes). Here rather than at each call site so a new billing path cannot forget it. What is about to change
/// is noted while saving and the cache is cleared only once the save has committed, so a request in between cannot put the
/// old plan straight back.
/// </summary>
public class FeatureAccessInvalidationInterceptor : SaveChangesInterceptor
{
    private readonly FeatureAccessCache _cache;
    private readonly HashSet<Guid> _tenants = new();
    private bool _all;

    public FeatureAccessInvalidationInterceptor(FeatureAccessCache cache) => _cache = cache;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush();
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        // Nothing committed, but a partial failure is not worth reasoning about: clearing is always safe.
        Flush();
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Flush();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Collect(DbContext? context)
    {
        if (context is null) return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            switch (entry.Entity)
            {
                case Subscription subscription:
                    _tenants.Add(subscription.TenantId);
                    break;
                case Plan or PlanFeature:
                    _all = true;
                    break;
            }
        }
    }

    private void Flush()
    {
        if (_all)
            _cache.InvalidateAll();
        else
            foreach (var tenantId in _tenants)
                _cache.InvalidateTenant(tenantId);

        _tenants.Clear();
        _all = false;
    }
}
