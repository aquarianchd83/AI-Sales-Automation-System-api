using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Hourly: for each tenant with a consent SMS still waiting for an answer, marks the invitations whose link ran out "No response" and - for a tenant
/// that turned reminders on - sends the reminders that have fallen due (two at most, only inside the tenant's daytime hours and the link's window).
/// One scope per tenant, with that tenant set, exactly as <see cref="TenantJobRunner"/> does it. A failure in one tenant is logged and never stops
/// the others; the next hour asks again.
/// </summary>
public class ConsentReminderJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConsentReminderJob> _logger;

    public ConsentReminderJob(IServiceScopeFactory scopeFactory, ILogger<ConsentReminderJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> tenantIds;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            tenantIds = await scope.ServiceProvider.GetRequiredService<IConsentInvitationService>().GetTenantsWithOpenInvitationsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Consent reminder pass could not list the tenants to look at");
            return;
        }

        foreach (var tenantId in tenantIds)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var services = scope.ServiceProvider;

                var status = await services.GetRequiredService<IApplicationDbContext>().Tenants
                    .Where(t => t.Id == tenantId)
                    .Select(t => (TenantStatus?)t.Status)
                    .FirstOrDefaultAsync(cancellationToken);
                if (status is null || !TenantStatusRules.AllowsBackgroundJobs(status.Value))
                    continue;

                services.GetRequiredService<ITenantContext>().SetTenant(tenantId);
                var result = await services.GetRequiredService<IConsentInvitationService>().ProcessRemindersAsync(cancellationToken);
                if (result.Sent + result.Failed + result.NoResponse > 0)
                    _logger.LogInformation("Consent reminders for tenant {TenantId}: {Result}", tenantId, result.Describe());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Consent reminder pass failed for tenant {TenantId}", tenantId);
            }
        }
    }
}
