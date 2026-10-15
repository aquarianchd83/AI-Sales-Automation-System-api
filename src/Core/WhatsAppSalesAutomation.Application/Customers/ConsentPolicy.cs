using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Customers;

/// <summary>
/// The status a new customer starts in, from the tenant's "is consent required" setting (Tenant.ConsentRequired). Every path that
/// creates a customer without recorded consent asks here, so the screens agree: not required is PendingOptIn (as it always was),
/// required is WaitingForConsent. Neither is consent - only OptedIn lets a customer be messaged.
/// </summary>
public static class ConsentPolicy
{
    public static OptInStatus InitialStatus(bool consentRequired) =>
        consentRequired ? OptInStatus.WaitingForConsent : OptInStatus.PendingOptIn;

    /// <summary>The initial status for a new customer of <paramref name="tenantId"/>; PendingOptIn when there is no tenant to ask.</summary>
    public static async Task<OptInStatus> InitialStatusAsync(
        IApplicationDbContext context, Guid? tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId is null)
            return OptInStatus.PendingOptIn;

        var required = await context.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.ConsentRequired)
            .FirstOrDefaultAsync(cancellationToken);

        return InitialStatus(required);
    }
}
