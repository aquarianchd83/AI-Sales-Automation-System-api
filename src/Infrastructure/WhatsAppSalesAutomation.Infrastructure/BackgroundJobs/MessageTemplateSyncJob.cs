using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.MessageTemplates;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Hourly two-way reconciliation between one tenant's local Campaign templates and Meta - see
/// MessageTemplateService.SyncWithMetaAsync for the actual push-then-pull rules (local templates are the
/// one place a template is created/edited; this job is what propagates that to Meta and brings Meta's
/// review status back). Both phases no-op safely against the Simulated WhatsApp client - the push half
/// fabricates a successful result there (see SimulatedWhatsAppClient.CreateMessageTemplateAsync), the
/// pull half returns an empty list - so this runs safely with or without a real WhatsApp Business Account
/// configured.
///
/// Runs for one tenant per execution - see CampaignInitialSenderJob's identical doc comment for the
/// per-tenant registration/TenantJobRunner reasoning. This is the job where the tenant scope matters
/// most: each tenant's WhatsAppServiceFactory resolution depends on ITenantContext actually being set to
/// that tenant before IWhatsAppService is resolved, so without it every tenant's sync would run against
/// whichever tenant (if any) happened to be ambient, or against Simulated for all of them.</summary>
public class MessageTemplateSyncJob
{
    private readonly TenantJobRunner _runner;
    private readonly ILogger<MessageTemplateSyncJob> _logger;

    public MessageTemplateSyncJob(TenantJobRunner runner, ILogger<MessageTemplateSyncJob> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    [DisableConcurrentExecutionPerTenant(timeoutInSeconds: 120)]
    public Task RunAsync(Guid tenantId) =>
        _runner.RunAsync(tenantId, TenantJobTypes.WhatsAppTemplateSync, async (services, cancellationToken) =>
        {
            var templateService = services.GetRequiredService<IMessageTemplateService>();
            var result = await templateService.SyncWithMetaAsync(cancellationToken: cancellationToken);

            // One line per failed push, at Warning - the summary below records that there were failures,
            // but not which template or why, and that is what someone debugging a stuck template needs.
            foreach (var failure in result.PushFailures)
                _logger.LogWarning(
                    "MessageTemplateSyncJob push failed for tenant {TenantId}, template {Name}: {Error}",
                    tenantId, failure.WhatsAppTemplateName, failure.ErrorMessage);

            var summary =
                $"pushed(created={result.CreatedCount} updated={result.UpdatedCount} failed={result.PushFailures.Count}) " +
                $"pulled(remote={result.RemoteTemplateCount} matched={result.MatchedCount} statusUpdated={result.StatusUpdatedCount})";

            return result.UnmatchedRemoteTemplateNames.Count > 0
                ? $"{summary} unmatched=[{string.Join(", ", result.UnmatchedRemoteTemplateNames)}]"
                : summary;
        });
}
