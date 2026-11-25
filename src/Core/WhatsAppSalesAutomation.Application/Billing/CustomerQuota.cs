using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Credits;

namespace WhatsAppSalesAutomation.Application.Billing;

/// <summary>A plan limit that is not a feature of its own, as the app shows it: "4 of 10 customers left".</summary>
public record PlanQuotaDto(string Key, string Label, int Limit, int Used, string Unit, string UnitSingular)
{
    public int Remaining => Math.Max(0, Limit - Used);

    public bool Exhausted => Remaining == 0;
}

/// <summary>
/// The most customers a tenant's plan lets it keep (<c>Plan.MaxCustomers</c>, set on the platform admin's plan editor; 0 = no limit). Only adding by
/// hand, importing and lead discovery are held to it: a customer who writes in on WhatsApp is always taken in, so a conversation is never lost.
/// </summary>
public static class CustomerQuota
{
    public const string Key = "Customers";

    /// <summary>The tenant's customer limit and how much of it is used, or null when its plan has none.</summary>
    public static async Task<PlanQuotaDto?> GetAsync(IApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken = default)
    {
        // A tenant with no plan row yet is on the Trial plan, as everywhere else.
        var planId = await context.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId).Select(s => s.PlanId).FirstOrDefaultAsync(cancellationToken);
        var max = planId is { } id
            ? await context.Plans.AsNoTracking().Where(p => p.Id == id).Select(p => (int?)p.MaxCustomers).FirstOrDefaultAsync(cancellationToken)
            : await context.Plans.AsNoTracking().Where(p => p.Code == PlatformFeatureCatalog.TrialPlanCode).Select(p => (int?)p.MaxCustomers).FirstOrDefaultAsync(cancellationToken);
        if (max is not > 0)
            return null;

        var used = await context.Customers.IgnoreQueryFilters().AsNoTracking().CountAsync(c => c.TenantId == tenantId && !c.IsDeleted, cancellationToken);
        return new PlanQuotaDto(Key, "Customers", max.Value, used, "customers", "customer");
    }

    /// <summary>What a tenant is told when its customer limit stops an add.</summary>
    public static string LimitReachedMessage(PlanQuotaDto quota) =>
        $"Your plan allows up to {quota.Limit} {(quota.Limit == 1 ? quota.UnitSingular : quota.Unit)}. Upgrade to Premium to add more.";

    /// <summary>Throws <see cref="PlanLimitExceededException"/> when the tenant cannot add <paramref name="adding"/> more customers.</summary>
    public static async Task EnsureCanAddAsync(IApplicationDbContext context, Guid tenantId, int adding = 1, CancellationToken cancellationToken = default)
    {
        if (await GetAsync(context, tenantId, cancellationToken) is { } quota && quota.Remaining < adding)
            throw new PlanLimitExceededException(LimitReachedMessage(quota));
    }
}

/// <summary>
/// The plan's team limit (<c>Plan.MaxUsers</c>, "Team members" on the plan editor) as a quota, so the app shows "N left" beside Users &amp; Roles.
/// Counted the way <c>PlanLimitsService.EnsureCanAddUserAsync</c> counts when it refuses a new user, and only for a tenant with a plan, as there.
/// </summary>
public static class UserQuota
{
    public const string Key = "Users";

    public static async Task<PlanQuotaDto?> GetAsync(IApplicationDbContext context, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var planId = await context.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.TenantId == tenantId).Select(s => s.PlanId).FirstOrDefaultAsync(cancellationToken);
        if (planId is not { } id)
            return null;
        var max = await context.Plans.AsNoTracking().Where(p => p.Id == id).Select(p => (int?)p.MaxUsers).FirstOrDefaultAsync(cancellationToken);
        if (max is null)
            return null;

        var used = await context.Users.IgnoreQueryFilters().AsNoTracking().CountAsync(u => u.TenantId == tenantId, cancellationToken);
        return new PlanQuotaDto(Key, "Users", max.Value, used, "users", "user");
    }
}
