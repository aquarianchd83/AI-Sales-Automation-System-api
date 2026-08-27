namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>Keeps the WhatsApp Cloud API access token from expiring by periodically exchanging it for
/// a fresh one via Meta's OAuth token-exchange endpoint. Called by WhatsAppTokenRefreshJob (a daily
/// Hangfire recurring job); exposed as its own service rather than inline in the job so the exchange
/// logic is unit-testable independent of Hangfire.</summary>
public interface IWhatsAppTokenRefreshService
{
    /// <summary>No-ops when WhatsApp:Provider isn't "Meta", when AppId/AppSecret aren't configured, or
    /// when the current token isn't due for renewal yet - safe to call unconditionally on a schedule.</summary>
    Task RefreshIfNeededAsync(CancellationToken cancellationToken = default);
}
