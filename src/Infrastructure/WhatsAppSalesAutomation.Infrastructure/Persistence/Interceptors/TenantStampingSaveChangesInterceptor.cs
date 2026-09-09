using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Common;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps <see cref="ITenantOwned.TenantId"/> on every new tenant-owned entity from the ambient
/// <see cref="ITenantContext"/> - mirrors <see cref="AuditableEntitySaveChangesInterceptor"/>'s
/// CreatedAt/UpdatedAt stamping, kept as its own interceptor since it is a separate concern. Also
/// guards against TenantId ever changing on an update: a cross-tenant reassignment should never
/// legitimately happen, so this fails loud rather than silently letting a bug move a row between
/// tenants.
/// </summary>
public class TenantStampingSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ITenantContext _tenantContext;

    public TenantStampingSaveChangesInterceptor(ITenantContext tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        StampTenants(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        StampTenants(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void StampTenants(DbContext? context)
    {
        if (context is null) return;

        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId != Guid.Empty)
                    continue;

                if (_tenantContext.TenantId is not { } tenantId)
                {
                    throw new InvalidOperationException(
                        $"Cannot save a new {entry.Entity.GetType().Name} without a tenant in scope. " +
                        "Either this request is unauthenticated/has no tenant claim, or a background job " +
                        "forgot to call ITenantContext.SetTenant before touching tenant-owned data.");
                }

                entry.Entity.TenantId = tenantId;
            }
            else if (entry.State == EntityState.Modified)
            {
                var originalTenantId = entry.OriginalValues.GetValue<Guid>(nameof(ITenantOwned.TenantId));
                if (originalTenantId != entry.Entity.TenantId)
                {
                    throw new InvalidOperationException(
                        $"{entry.Entity.GetType().Name} cannot be reassigned from tenant {originalTenantId} " +
                        $"to {entry.Entity.TenantId} - TenantId is immutable after creation.");
                }
            }
        }
    }
}
