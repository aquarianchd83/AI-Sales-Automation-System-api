using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>What "on a free trial" means, in one place: the tenant's status is Trial and the trial has not ended (a
/// trial with no end date, as an operator might leave one, has not ended). Onboarding counts a trial as having a plan,
/// and so does creating a customer package.</summary>
public static class TenantTrial
{
    public static bool IsActive(Tenant tenant, DateTime nowUtc) =>
        tenant.Status == TenantStatus.Trial && (tenant.TrialEndsAtUtc is null || tenant.TrialEndsAtUtc > nowUtc);
}
