using Hangfire;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.MessageTemplates;

namespace WhatsAppSalesAutomation.Infrastructure.BackgroundJobs;

/// <summary>Hourly two-way reconciliation between local Campaign templates and Meta - see
/// MessageTemplateService.SyncWithMetaAsync for the actual push-then-pull rules (local templates are
/// the one place a template is created/edited; this job is what propagates that to Meta and brings
/// Meta's review status back). Both phases no-op safely against the Simulated WhatsApp client - the
/// push half fabricates a successful result there (see SimulatedWhatsAppClient.CreateMessageTemplateAsync),
/// the pull half returns an empty list - so this runs safely with or without a real WhatsApp Business
/// Account configured.</summary>
public class MessageTemplateSyncJob
{
    private readonly IMessageTemplateService _templateService;
    private readonly ILogger<MessageTemplateSyncJob> _logger;

    public MessageTemplateSyncJob(IMessageTemplateService templateService, ILogger<MessageTemplateSyncJob> logger)
    {
        _templateService = templateService;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    public async Task RunAsync()
    {
        try
        {
            var result = await _templateService.SyncWithMetaAsync();

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
        }
        catch (Exception ex)
        {
            // Never let a Meta-side hiccup (rate limit, transient outage) turn into a permanently-
            // failing Hangfire job - same reasoning as WhatsAppTokenRefreshJob's own catch-all.
            _logger.LogError(ex, "MessageTemplateSyncJob failed unexpectedly");
        }
    }
}
