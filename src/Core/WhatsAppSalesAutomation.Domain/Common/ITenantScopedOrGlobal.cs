namespace WhatsAppSalesAutomation.Domain.Common;

/// <summary>
/// An entity that belongs EITHER to one tenant (<see cref="TenantId"/> set) OR to the platform itself
/// (<see cref="TenantId"/> NULL, readable by every tenant). Distinct from <see cref="ITenantOwned"/>,
/// whose query filter is a plain equality and therefore cannot express "or global" - <c>NULL == @guid</c>
/// is never true in SQL, so a global row is invisible to an <see cref="ITenantOwned"/> filter rather
/// than shared by it.
///
/// <c>ApplicationDbContext</c> gives these a different filter:
/// <code>e.TenantId == null || e.TenantId == _tenantContext.TenantId</code>
///
/// The NULL branch is READ-ONLY for a tenant:
/// <see cref="Infrastructure.Persistence.Interceptors.TenantStampingSaveChangesInterceptor"/> refuses
/// to insert or update a NULL-TenantId row unless <c>ITenantContext.IsPlatformSuperAdmin</c>. That
/// interceptor rule - not a controller check - is what makes "a tenant cannot author platform policy"
/// true even for a code path nobody remembered to guard.
///
/// An entity must implement this or <see cref="ITenantOwned"/>, never both: EF Core permits one
/// <c>HasQueryFilter</c> per entity and silently keeps the last one registered, so implementing both
/// would mean one of the two filters quietly does nothing. <c>ApplicationDbContext</c> asserts this at
/// model-build time rather than leaving it to review.
/// </summary>
public interface ITenantScopedOrGlobal
{
    /// <summary>NULL = GLOBAL (platform knowledge, readable by every tenant). Non-null = that tenant's
    /// private data. Nullable is the whole point of this interface.</summary>
    Guid? TenantId { get; set; }
}
