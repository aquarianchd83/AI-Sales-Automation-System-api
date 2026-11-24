using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Twice a day (platform-global, like <see cref="WhatsAppBridgeHealthJob"/>): finds the QR-bridge tenants whose WhatsApp session has dropped and tells each one, once per
/// disconnect, so they scan a new QR before follow-ups pile up. The notice goes out through the PLATFORM's own WhatsApp number (short text) and email account
/// (full text) - the tenant's own session is the one that is down - and the tenant's credit wallet pays for it, at the price of one template message.
/// An alert is never held back for lack of credit: the wallet is charged what it can cover. One tenant's failure never stops the pass.
/// </summary>
public class WhatsAppSessionAlertJob
{
    /// <summary>A session that has dropped. Never-paired (NotConnected) and mid-pairing states are not a "disconnect" and are not alerted.</summary>
    internal static bool IsDisconnected(string? status) => status is BridgeStatuses.Disconnected or BridgeStatuses.ConnectionFailed;

    private readonly IWhatsAppChannelStore _channels;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsSnapshot<WhatsAppProviderOptions> _providerOptions;
    private readonly IOptionsSnapshot<WhatsAppBridgeOptions> _bridgeOptions;
    private readonly ILogger<WhatsAppSessionAlertJob> _logger;

    public WhatsAppSessionAlertJob(
        IWhatsAppChannelStore channels, IServiceScopeFactory scopeFactory, IOptionsSnapshot<WhatsAppProviderOptions> providerOptions,
        IOptionsSnapshot<WhatsAppBridgeOptions> bridgeOptions, ILogger<WhatsAppSessionAlertJob> logger)
    {
        _channels = channels;
        _scopeFactory = scopeFactory;
        _providerOptions = providerOptions;
        _bridgeOptions = bridgeOptions;
        _logger = logger;
    }

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync()
    {
        var alerted = 0;
        foreach (var tenantId in await _channels.ListTenantIdsAsync())
        {
            try
            {
                // Only tenants actually served by the bridge: an old channel row on a tenant that went back to Meta is not a problem to report.
                var provider = WhatsAppProviderRouting.Resolve(
                    _providerOptions.Value.Active, _providerOptions.Value.TenantOverrides, _bridgeOptions.Value.PilotTenantIds, tenantId);
                if (provider != WhatsAppProviders.QrBridge)
                    continue;

                var channel = await _channels.GetAsync(tenantId);
                if (channel is null || !IsDisconnected(channel.Status))
                    continue;

                if (await AlertAsync(tenantId, channel))
                    alerted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "WhatsApp session alert failed for tenant {TenantId}", tenantId);
            }
        }

        if (alerted > 0)
            _logger.LogInformation("WhatsApp session alerts: {Alerted} tenant(s) told their session is disconnected", alerted);
    }

    private async Task<bool> AlertAsync(Guid tenantId, WhatsAppChannelRecord channel)
    {
        using var scope = _scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        services.GetRequiredService<ITenantContext>().SetTenant(tenantId);

        // One alert per disconnect: the episode is when the session went down, so the next pass (and the next day) stays quiet until it drops again.
        var episode = $"session-{channel.StatusUpdatedAtUtc:yyyyMMddHHmmss}";

        var sent = await services.GetRequiredService<ITenantNotifier>().NotifyAsync(
            BuildRequest(tenantId, channel.ConnectedPhone, episode));
        if (!sent)
            return false;

        await ChargeAsync(services, tenantId, episode);
        return true;
    }

    internal static TenantNotificationRequest BuildRequest(Guid tenantId, string? connectedPhone, string episode)
    {
        var number = string.IsNullOrWhiteSpace(connectedPhone) ? "aapka WhatsApp number" : $"aapka WhatsApp number ({connectedPhone})";
        return new TenantNotificationRequest(
            tenantId, TenantNotificationKind.WhatsAppSessionDisconnected, null, episode,
            "Aapka WhatsApp disconnect ho gaya hai",
            EmailBody(number),
            AlsoWhatsApp: true,
            PlainTextFallback: true,
            WhatsAppBody: $"{char.ToUpperInvariant(number[0])}{number[1..]} AiSaarthi se disconnect ho gaya hai, isliye follow-up messages ruke hue hain.");
    }

    private static string EmailBody(string number) =>
        $"{char.ToUpperInvariant(number[0])}{number[1..]} AiSaarthi se disconnect ho gaya hai, isliye abhi aapke customers ko follow-up messages nahi ja rahe.\n\n" +
        "Dobara connect karne ke liye:\n" +
        "1. AiSaarthi mein login karein\n" +
        "2. Settings > WhatsApp Connection par jaayein\n" +
        "3. Naya QR code scan karein (Phone > WhatsApp > Linked Devices > Link a Device)\n\n" +
        "Connect hote hi ruke hue messages apne aap dobara chalu ho jayenge.\n\n" +
        "Madad chahiye to is email ka reply karein.\nTeam AiSaarthi";

    /// <summary>The tenant's own wallet pays, at the price of one template message. Never throws and never blocks the alert: what the wallet cannot cover is simply not taken.</summary>
    private async Task ChargeAsync(IServiceProvider services, Guid tenantId, string episode)
    {
        try
        {
            var credits = await services.GetRequiredService<ICreditCostService>().GetCreditsPerUnitAsync(CreditScenario.WhatsAppTemplateSend);
            if (credits <= 0)
                return;

            await services.GetRequiredService<IQuotaLedgerService>().ConsumeAsync(new ConsumeRequest(
                tenantId, QuotaType.Credits, credits, $"session-alert:{episode}", "SessionDisconnectAlert", episode,
                Note: "WhatsApp session disconnected alert (sent from the platform's WhatsApp and email accounts)",
                AllowPartial: true, UnitsAreCredits: true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not charge the session disconnect alert to tenant {TenantId}", tenantId);
        }
    }
}
