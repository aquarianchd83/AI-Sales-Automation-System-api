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
///
/// Handles <see cref="ITenantScopedOrGlobal"/> too, where the rules are deliberately different: a
/// NULL TenantId is meaningful there (it means GLOBAL, platform-owned), so it cannot simply be
/// stamped over. Instead, writing NULL requires <see cref="ITenantContext.IsPlatformSuperAdmin"/>.
/// That rule living HERE rather than in a controller is the point - it holds for a background job, a
/// seeder and a code path nobody remembered to guard, which is what makes "a tenant cannot author
/// platform policy" a property of the system rather than a convention.
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

        StampTenantOwned(context);
        GuardTenantScopedOrGlobal(context);
    }

    private void StampTenantOwned(DbContext context)
    {
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

    /// <summary>
    /// Enforces the read-only-ness of the GLOBAL branch for tenant callers.
    ///
    /// On insert: a null TenantId is stamped with the current tenant, exactly as for
    /// <see cref="ITenantOwned"/> - so a tenant writing an article gets a tenant-scoped one by
    /// default and has to be a SuperAdmin to get anything else. A SuperAdmin (no tenant in scope)
    /// leaves it null, which is how platform knowledge is authored.
    ///
    /// On update: neither direction of a scope change is allowed for a tenant. Moving a row from
    /// their tenant to GLOBAL would be authoring platform policy; moving a GLOBAL row to their tenant
    /// would be taking a copy of it out of every other tenant's reach. A SuperAdmin may do either.
    /// </summary>
    private void GuardTenantScopedOrGlobal(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<ITenantScopedOrGlobal>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.TenantId is not null)
                {
                    // An explicit tenant was supplied. Only a SuperAdmin may write one that is not
                    // the tenant in scope - otherwise this is a cross-tenant insert.
                    if (!_tenantContext.IsPlatformSuperAdmin &&
                        _tenantContext.TenantId is { } scoped &&
                        entry.Entity.TenantId != scoped)
                    {
                        throw new InvalidOperationException(
                            $"Cannot save a new {entry.Entity.GetType().Name} for tenant {entry.Entity.TenantId} " +
                            $"while acting as tenant {scoped}.");
                    }

                    continue;
                }

                if (_tenantContext.IsPlatformSuperAdmin)
                    continue;   // Authoring GLOBAL knowledge - the one caller allowed to.

                if (_tenantContext.TenantId is not { } tenantId)
                {
                    throw new InvalidOperationException(
                        $"Cannot save a new {entry.Entity.GetType().Name} without a tenant in scope and " +
                        "without PlatformSuperAdmin. A NULL TenantId on this entity means GLOBAL " +
                        "(platform-owned), so it is never a safe default for an unattributed write - " +
                        "either set ITenantContext.SetTenant, or perform the write as a SuperAdmin.");
                }

                entry.Entity.TenantId = tenantId;
            }
            else if (entry.State == EntityState.Modified)
            {
                var original = entry.OriginalValues.GetValue<Guid?>(nameof(ITenantScopedOrGlobal.TenantId));
                if (original == entry.Entity.TenantId)
                    continue;

                if (_tenantContext.IsPlatformSuperAdmin)
                    continue;

                throw new InvalidOperationException(
                    $"{entry.Entity.GetType().Name} cannot be moved from " +
                    $"{(original is null ? "GLOBAL" : original.ToString())} to " +
                    $"{(entry.Entity.TenantId is null ? "GLOBAL" : entry.Entity.TenantId.ToString())} - " +
                    "only a PlatformSuperAdmin may change an entity's tenant scope.");
            }
        }
    }
}
