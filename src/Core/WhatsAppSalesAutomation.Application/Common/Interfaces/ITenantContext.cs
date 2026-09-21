namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>
/// The tenant a unit of work (an HTTP request, or one iteration of a per-tenant background job loop)
/// is operating as. Consulted by <c>ApplicationDbContext</c>'s global query filter for every
/// <c>ITenantOwned</c> entity and by <c>TenantStampingSaveChangesInterceptor</c> on insert - see both
/// for the actual enforcement.
///
/// Defaults from <see cref="ICurrentUserService.TenantId"/> (the JWT claim) for an ordinary
/// authenticated request. <see cref="SetTenant"/> exists for the two cases that are not "one HTTP
/// request, one tenant": a per-tenant background-job loop, which creates a fresh DI scope per tenant
/// and calls this once at the top of each iteration, and the WhatsApp webhook endpoint, which is
/// unauthenticated and only learns which tenant it's handling after parsing the payload.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }

    /// <summary>True for a <c>PlatformSuperAdmin</c> (the SaaS operator) - such a request has no
    /// single tenant, so it sees zero tenant-owned rows through the ordinary query filter, by design;
    /// platform-wide listings are separate, explicit, audited endpoints using
    /// <c>IgnoreQueryFilters()</c>, not an implicit "see everything" here.</summary>
    bool IsPlatformSuperAdmin { get; }

    void SetTenant(Guid tenantId);

    /// <summary>
    /// Runs the rest of this scope AS the platform, for a background job that maintains GLOBAL
    /// (TenantId NULL) data - e.g. indexing a platform policy article. Such a job has no tenant claim
    /// and no JWT, so without this the stamping interceptor correctly refuses its writes: NULL means
    /// "platform-owned" and must never be a default for an unattributed write.
    ///
    /// Deliberately an explicit call rather than "no tenant means platform": the interceptor's whole
    /// safety property is that forgetting SetTenant fails loudly. Only code that means to act as the
    /// platform says so, and grep finds every such place.
    ///
    /// The default implementation throws, so a test fake or alternative context that does not
    /// support it fails loudly instead of silently granting platform rights.
    /// </summary>
    void EnterPlatformScope() =>
        throw new NotSupportedException($"{GetType().Name} does not support platform scope.");
}
