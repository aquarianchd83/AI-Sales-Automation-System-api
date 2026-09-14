using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Keeps one tenant's WhatsApp access token from expiring - see TenantWhatsAppTokenRefreshService for
/// the actual Meta exchange. Registered per tenant (<c>whatsapp-token-refresh:{tenantId}</c>) like every
/// other job in TenantJobCatalog, so each tenant's refresh has its own schedule, its own last-run
/// outcome, and can be paused or run on its own from the Platform Admin Console.
///
/// Was a single platform-global job until each tenant brought its own token. That version refreshed one
/// shared WhatsAppAccessTokenState row that no send had read since BYO-WABA, while leaving every
/// tenant's real token to expire unnoticed.
///
/// Daily by default. A no-op most days - Meta is only called once a token is inside its 10-day refresh
/// window, and never for a token Meta has reported does not expire - so the cadence is generous rather
/// than tightly timed. A Meta rejection surfaces as a Failed run, counted toward the tenant's consecutive
/// failures, rather than being logged and forgotten.
/// </summary>
public class WhatsAppTokenRefreshJob
{
    private readonly TenantJobRunner _runner;

    public WhatsAppTokenRefreshJob(TenantJobRunner runner)
    {
        _runner = runner;
    }

    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 60)]
    public Task RunAsync(Guid tenantId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.WhatsAppTokenRefresh, async (services, cancellationToken) =>
        {
            var refresh = services.GetRequiredService<ITenantWhatsAppTokenRefreshService>();
            var result = await refresh.RefreshAsync(tenantId, cancellationToken);
            return result.Summary;
        });
}
