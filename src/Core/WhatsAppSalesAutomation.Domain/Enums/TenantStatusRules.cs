namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Rules about <see cref="TenantStatus"/> that more than one layer has to agree on, kept here
/// rather than re-expressed at each call site.</summary>
public static class TenantStatusRules
{
    /// <summary>
    /// The statuses whose tenants may have recurring background jobs registered and running - a
    /// <see cref="TenantStatus.Trial"/> tenant's campaigns must still send, everything else must not.
    ///
    /// Deliberately a whitelist, not a "not Suspended and not Cancelled" exclusion list: the exclusion
    /// form is what let <see cref="TenantStatus.Deleted"/> (added later, by the Platform Admin Console
    /// phase) keep sending campaigns and syncing templates for a tenant an operator had explicitly
    /// terminated. With a whitelist, a status added by a future release is non-running until someone
    /// deliberately lists it here, which is the safe default.
    ///
    /// An array rather than a method so the same single definition serves both an EF Core query
    /// (<c>Contains</c> translates to a SQL <c>IN</c>) and an in-memory check.
    /// </summary>
    public static readonly TenantStatus[] BackgroundJobsEligible = { TenantStatus.Trial, TenantStatus.Active };

    /// <summary>Whether this tenant's recurring jobs should be registered and allowed to run.</summary>
    public static bool AllowsBackgroundJobs(TenantStatus status) => BackgroundJobsEligible.Contains(status);
}
