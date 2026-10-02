using Microsoft.Extensions.DependencyInjection;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Enums;
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
///
/// The tenant is told too (unlike most job failures, which only the operator sees): only the tenant can paste a new token
/// or add the App ID/Secret, and when the token lapses every send stops. A failed refresh, or a token inside its refresh
/// window with no way to renew it, raises one in-app/email notice a day until it is sorted out.
/// </summary>
public class WhatsAppTokenRefreshJob
{
    private readonly TenantJobRunner _runner;

    public WhatsAppTokenRefreshJob(TenantJobRunner runner)
    {
        _runner = runner;
    }

    /// <summary>Inside this many days of expiring, a token nothing can renew is worth a daily reminder.</summary>
    private static readonly TimeSpan WarnWindow = TimeSpan.FromDays(10);

    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 60)]
    public Task RunAsync(Guid tenantId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.WhatsAppTokenRefresh, async (services, cancellationToken) =>
        {
            var refresh = services.GetRequiredService<ITenantWhatsAppTokenRefreshService>();

            TenantWhatsAppTokenRefreshResult result;
            try
            {
                result = await refresh.RefreshAsync(tenantId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Still a Failed run for the platform alert below this - the tenant is told as well, then it is rethrown.
                await NotifyTenantAsync(services, tenantId,
                    "Your WhatsApp connection needs attention",
                    $"We couldn't renew your WhatsApp access token: {Shorten(ex.Message)} Messages stop sending when it expires. " +
                    "Open Settings and paste a fresh access token.",
                    cancellationToken);
                throw;
            }

            if (result.Status == TenantWhatsAppTokenRefreshStatus.MissingAppCredentials && result.ExpiresAtUtc is { } expiresAt
                && expiresAt - DateTime.UtcNow <= WarnWindow)
            {
                var lapsed = expiresAt <= DateTime.UtcNow;
                await NotifyTenantAsync(services, tenantId,
                    lapsed ? "Your WhatsApp access token has expired" : "Your WhatsApp access token is about to expire",
                    (lapsed ? $"Your WhatsApp access token expired on {expiresAt:d MMM yyyy}, so messages can't be sent. " : $"Your WhatsApp access token expires on {expiresAt:d MMM yyyy}, after which messages can't be sent. ") +
                    "It can't be renewed automatically because the App ID or App Secret is missing. Add them in Settings, or paste a fresh token.",
                    cancellationToken);
            }

            return result.Summary;
        });

    /// <summary>One notice a day (the episode is the date), so a daily job that keeps failing reminds without flooding. Never throws.</summary>
    private static Task NotifyTenantAsync(IServiceProvider services, Guid tenantId, string title, string body, CancellationToken cancellationToken) =>
        services.GetRequiredService<ITenantNotifier>().NotifyAsync(
            new TenantNotificationRequest(tenantId, TenantNotificationKind.WhatsAppTokenFailing, null, $"token-{DateTime.UtcNow:yyyyMMdd}", title, body, AlsoWhatsApp: false),
            cancellationToken);

    private static string Shorten(string message) => message.Length <= 300 ? message : message[..299] + "…";
}
