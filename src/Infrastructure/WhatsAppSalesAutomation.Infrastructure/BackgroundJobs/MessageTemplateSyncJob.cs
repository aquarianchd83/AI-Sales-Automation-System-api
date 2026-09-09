using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.MessageTemplates;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Hourly two-way reconciliation between local Campaign templates and Meta - see
/// MessageTemplateService.SyncWithMetaAsync for the actual push-then-pull rules (local templates are
/// the one place a template is created/edited; this job is what propagates that to Meta and brings
/// Meta's review status back). Both phases no-op safely against the Simulated WhatsApp client - the
/// push half fabricates a successful result there (see SimulatedWhatsAppClient.CreateMessageTemplateAsync),
/// the pull half returns an empty list - so this runs safely with or without a real WhatsApp Business
/// Account configured.
///
/// Fans out over every active tenant - see CampaignInitialSenderJob's identical doc comment for the
/// TenantJobRunner/scope-per-tenant reasoning. This is the job where that fan-out matters most of the
/// three "no real credentials configured yet" outcomes look the same either way, but each tenant's own
/// WhatsAppServiceFactory resolution (see Phase 2 of the SaaS conversion plan) now depends on
/// ITenantContext actually being set to that tenant before IWhatsAppService is resolved - without the
/// per-tenant scope here, every tenant's sync would silently run against whichever tenant (if any)
/// happened to be ambient, or against Simulated for all of them.</summary>
public class MessageTemplateSyncJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MessageTemplateSyncJob> _logger;

    public MessageTemplateSyncJob(IServiceScopeFactory scopeFactory, ILogger<MessageTemplateSyncJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    public async Task RunAsync()
    {
        await TenantJobRunner.RunForEachActiveTenantAsync(_scopeFactory, _logger, nameof(MessageTemplateSyncJob), async (services, cancellationToken) =>
        {
            var templateService = services.GetRequiredService<IMessageTemplateService>();
            var result = await templateService.SyncWithMetaAsync(cancellationToken: cancellationToken);

            if (result.CreatedCount > 0 || result.UpdatedCount > 0 || result.PushFailures.Count > 0
                || result.StatusUpdatedCount > 0 || result.UnmatchedRemoteTemplateNames.Count > 0)
            {
                _logger.LogInformation(
                    "MessageTemplateSyncJob: pushed(created={Created} updated={Updated} failed={Failed}) " +
                    "pulled(remote={Remote} matched={Matched} statusUpdated={StatusUpdated} unmatched=[{Unmatched}])",
                    result.CreatedCount, result.UpdatedCount, result.PushFailures.Count,
                    result.RemoteTemplateCount, result.MatchedCount, result.StatusUpdatedCount,
                    string.Join(", ", result.UnmatchedRemoteTemplateNames));

                foreach (var failure in result.PushFailures)
                    _logger.LogWarning("MessageTemplateSyncJob push failed for {Name}: {Error}", failure.WhatsAppTemplateName, failure.ErrorMessage);
            }
        });
    }
}
