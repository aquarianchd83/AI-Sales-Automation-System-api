using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Messaging;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>
/// Thin Hangfire-triggered wrapper - all the actual logic lives in <see cref="ICampaignSendService"/>
/// so it stays framework-agnostic and directly unit-testable. DisableConcurrentExecution is the third
/// line of defense against a double send, behind the idempotency key and its unique DB index (see
/// CampaignSendService's remarks).
///
/// Fans out over every active tenant via <see cref="TenantJobRunner"/> - a fresh DI scope per tenant
/// with <c>ITenantContext</c> set before <see cref="ICampaignSendService"/> is resolved from it, so the
/// existing tenant-scoped query filters do the rest without ICampaignSendService itself needing to
/// know tenancy exists. RunAsync's own signature (no parameters) is unchanged, so
/// RecurringJobsRegistrar needs no change either - the fan-out is entirely internal to this class.
/// </summary>
public class CampaignInitialSenderJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CampaignInitialSenderJob> _logger;

    public CampaignInitialSenderJob(IServiceScopeFactory scopeFactory, ILogger<CampaignInitialSenderJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 280)]
    public async Task RunAsync()
    {
        int considered = 0, sent = 0, failed = 0, skipped = 0;

        await TenantJobRunner.RunForEachActiveTenantAsync(_scopeFactory, _logger, nameof(CampaignInitialSenderJob), async (services, cancellationToken) =>
        {
            var sendService = services.GetRequiredService<ICampaignSendService>();
            var result = await sendService.ProcessInitialSendsAsync(cancellationToken: cancellationToken);
            considered += result.Considered;
            sent += result.Sent;
            failed += result.Failed;
            skipped += result.Skipped;
        });

        if (considered > 0)
            _logger.LogInformation(
                "CampaignInitialSenderJob: considered={Considered} sent={Sent} failed={Failed} skipped={Skipped}",
                considered, sent, failed, skipped);
    }
}
