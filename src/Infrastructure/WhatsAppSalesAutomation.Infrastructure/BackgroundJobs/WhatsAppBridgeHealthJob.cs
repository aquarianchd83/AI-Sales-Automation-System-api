using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Platform-global (like SocialAdSyncJob: it walks every tenant that has something to check). Reconciles each tenant's QR channel with what the bridge itself says,
/// because a connection event the bridge failed to deliver would otherwise leave the stored status wrong - and "Connected" is what lets sends go out. It never
/// changes what a message does: it only corrects the status the send path already trusts. One tenant's failure never stops the pass.
/// </summary>
public class WhatsAppBridgeHealthJob
{
    private readonly IWhatsAppChannelStore _channels;
    private readonly IWhatsAppChannelService _service;
    private readonly ILogger<WhatsAppBridgeHealthJob> _logger;

    public WhatsAppBridgeHealthJob(IWhatsAppChannelStore channels, IWhatsAppChannelService service, ILogger<WhatsAppBridgeHealthJob> logger)
    {
        _channels = channels;
        _service = service;
        _logger = logger;
    }

    [Hangfire.DisableConcurrentExecution(timeoutInSeconds: 240)]
    public async Task RunAsync()
    {
        var tenantIds = await _channels.ListTenantIdsAsync();
        foreach (var tenantId in tenantIds)
        {
            try
            {
                var before = (await _channels.GetAsync(tenantId))?.Status;
                var after = (await _service.GetStatusAsync(tenantId)).Status;
                if (before != after)
                    _logger.LogWarning("QR channel for tenant {TenantId} changed from {Before} to {After} on the health pass", tenantId, before, after);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "QR channel health check failed for tenant {TenantId}", tenantId);
            }
        }
    }
}
