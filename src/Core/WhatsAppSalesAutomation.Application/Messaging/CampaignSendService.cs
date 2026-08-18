using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Messaging;

/// <summary>
/// The idempotent campaign send pipeline. Every send is guarded three ways: an application-level
/// existence check on the idempotency key before insert, the key's unique DB index as the race-proof
/// backstop, and <c>[DisableConcurrentExecution]</c> on the Hangfire jobs that call this so two ticks
/// of the same job never overlap.
///
/// Known limitation, stated rather than hidden: the window between "Message row saved as Queued" and
/// "WhatsApp API call returns" is not crash-safe against a double send. If the process dies in that
/// window after WhatsApp actually accepted the message, our own idempotency key correctly prevents a
/// second Message row, but <see cref="RetryFailedSendsAsync"/> will still retry the stuck Queued row
/// and could send a second WhatsApp message for it. True dedup on WhatsApp's side needs a
/// client-supplied idempotency key, which Meta's template send API does not offer, or delivery
/// confirmation via status webhooks - Phase 4. The staleness threshold below only narrows the window;
/// it does not close it.
/// </summary>
public class CampaignSendService : ICampaignSendService
{
    /// <summary>How long a Message may sit at Queued before the retry job treats it as stuck rather
    /// than "still in flight" - see the class remarks.</summary>
    private static readonly TimeSpan StaleQueuedThreshold = TimeSpan.FromMinutes(10);

    private readonly IApplicationDbContext _context;
    private readonly IWhatsAppService _whatsApp;
    private readonly IDateTimeProvider _dateTime;
    private readonly MessagingOptions _options;
    private readonly ILogger<CampaignSendService> _logger;

    public CampaignSendService(
        IApplicationDbContext context,
        IWhatsAppService whatsApp,
        IDateTimeProvider dateTime,
        IOptions<MessagingOptions> options,
        ILogger<CampaignSendService> logger)
    {
        _context = context;
        _whatsApp = whatsApp;
        _dateTime = dateTime;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SendRunResult> ProcessInitialSendsAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;

        // ScheduledStartAt is pinned to IST (see Campaign.ScheduledStartAt), so the "is it due yet"
        // comparison uses IstNow; StartedAt is a true system timestamp and stays UTC (now).
        var istNow = _dateTime.IstNow;

        // Plain load-and-save rather than ExecuteUpdateAsync: that extension lives in
        // Microsoft.EntityFrameworkCore.Relational, which Application deliberately does not
        // reference (the SQL Server/relational provider stays in Infrastructure). The number of
        // Scheduled campaigns due at any one tick is small, so this costs nothing in practice.
        var dueToStart = await _context.Campaigns
            .Where(c => c.Status == CampaignStatus.Scheduled && c.ScheduledStartAt != null && c.ScheduledStartAt <= istNow)
            .ToListAsync(cancellationToken);

        if (dueToStart.Count > 0)
        {
            foreach (var due in dueToStart)
            {
                due.Status = CampaignStatus.Running;
                due.StartedAt = now;
            }

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Promoted {Count} scheduled campaign(s) to Running", dueToStart.Count);
        }

        var candidates = await _context.CampaignCustomers
            .Where(cc => cc.Status == CampaignCustomerStatus.Pending)
            .Join(_context.Campaigns.Where(c => c.Status == CampaignStatus.Running), cc => cc.CampaignId, c => c.Id, (cc, c) => cc)
            .OrderBy(cc => cc.CreatedAt)
            .Take(_options.MaxSendsPerRun)
            .Select(cc => cc.Id)
            .ToListAsync(cancellationToken);

        var result = SendRunResult.Empty;
        foreach (var ccId in candidates)
            result = Add(result, await ProcessOneAsync(ccId, fromStepNumber: 0, now, cancellationToken));

        return result;
    }

    public async Task<SendRunResult> ProcessFollowUpsAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;

        var due = await _context.CampaignCustomers
            .Where(cc => cc.Status == CampaignCustomerStatus.AwaitingResponse && cc.NextFollowUpDueAt != null && cc.NextFollowUpDueAt <= now)
            .Join(_context.Campaigns.Where(c => c.Status == CampaignStatus.Running), cc => cc.CampaignId, c => c.Id, (cc, c) => cc)
            .OrderBy(cc => cc.NextFollowUpDueAt)
            .Take(_options.MaxSendsPerRun)
            .Select(cc => new { cc.Id, cc.CurrentStepNumber })
            .ToListAsync(cancellationToken);

        var result = SendRunResult.Empty;
        foreach (var item in due)
            result = Add(result, await ProcessOneAsync(item.Id, item.CurrentStepNumber + 1, now, cancellationToken));

        return result;
    }

    public async Task<SendRunResult> RetryFailedSendsAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var staleBefore = now - StaleQueuedThreshold;

        var messageIds = await _context.Messages
            .Where(m =>
                (m.Status == MessageStatus.Failed && m.AttemptCount < _options.MaxRetryAttempts && (m.NextAttemptAt == null || m.NextAttemptAt <= now)) ||
                (m.Status == MessageStatus.Queued && m.CreatedAt <= staleBefore))
            .OrderBy(m => m.CreatedAt)
            .Take(_options.MaxSendsPerRun)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        var result = SendRunResult.Empty;
        foreach (var messageId in messageIds)
            result = Add(result, await RetryOneAsync(messageId, now, cancellationToken));

        return result;
    }

    /// <summary>Loads one campaign customer fresh and attempts the lowest active step at or after
    /// <paramref name="fromStepNumber"/> - used for both initial sends (fromStepNumber 0) and
    /// follow-ups (fromStepNumber = CurrentStepNumber + 1).</summary>
    private async Task<SendRunResult> ProcessOneAsync(Guid campaignCustomerId, int fromStepNumber, DateTime now, CancellationToken cancellationToken)
    {
        var cc = await _context.CampaignCustomers.FirstOrDefaultAsync(x => x.Id == campaignCustomerId, cancellationToken);
        if (cc is null)
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };

        var campaign = await _context.Campaigns
            .Include(c => c.Steps).ThenInclude(s => s.StepMedia)
            .FirstOrDefaultAsync(c => c.Id == cc.CampaignId, cancellationToken);

        // ProcessInitialSendsAsync/ProcessFollowUpsAsync already select candidates only from Running
        // campaigns, but that selection and this method's own campaign load are two separate queries -
        // a Stop call landing in between them would otherwise still be able to slip a send through.
        // Re-checking here, not just at selection time, closes that window. Left untouched rather than
        // force-completed: StopAsync already decided what happens to this customer's status.
        if (campaign is not null && campaign.Status != CampaignStatus.Running)
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };

        // The lowest active step at or after fromStepNumber, not exactly fromStepNumber - a step can
        // be deactivated (CampaignStep.IsActive) without being removed, and CampaignService no longer
        // allows removing/adding steps out of sequence, but it still allows deactivating one in place.
        // Requiring an exact match here would treat that gap as "nothing left to send" and silently
        // drop every follow-up after it instead of skipping over the disabled one.
        var step = campaign?.Steps
            .Where(s => s.StepNumber >= fromStepNumber && s.IsActive)
            .OrderBy(s => s.StepNumber)
            .FirstOrDefault();

        if (campaign is null || step is null)
        {
            // No further active step: nothing more will ever be sent to this customer in this
            // campaign, so there is no point leaving them "awaiting" one.
            if (cc.Status == CampaignCustomerStatus.AwaitingResponse)
            {
                cc.Status = CampaignCustomerStatus.Completed;
                cc.NextFollowUpDueAt = null;
                await _context.SaveChangesAsync(cancellationToken);
            }

            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };
        }

        // IgnoreQueryFilters not needed: a soft-deleted customer simply will not be found, and
        // "not found" is handled the same as "not opted in" below.
        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == cc.CustomerId, cancellationToken);
        if (customer is null || customer.OptInStatus != OptInStatus.OptedIn)
        {
            cc.Status = CampaignCustomerStatus.OptedOut;
            cc.StoppedReason = "Customer is not opted in";
            cc.NextFollowUpDueAt = null;
            await _context.SaveChangesAsync(cancellationToken);
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };
        }

        var idempotencyKey = BuildIdempotencyKey(cc.Id, step.StepNumber);
        var alreadyQueued = await _context.Messages.AnyAsync(m => m.IdempotencyKey == idempotencyKey, cancellationToken);
        if (alreadyQueued)
        {
            // A previous tick already reserved this key (most likely it crashed between saving
            // Queued and getting a WhatsApp response) - the retry job owns it from here, not this one.
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };
        }

        var template = step.MessageTemplateId.HasValue
            ? await _context.MessageTemplates.FirstOrDefaultAsync(t => t.Id == step.MessageTemplateId, cancellationToken)
            : null;
        if (template is null || template.WhatsAppTemplateStatus != WhatsAppTemplateStatus.Approved || !template.IsActive)
        {
            _logger.LogWarning(
                "Skipping step {StepNumber} for campaign customer {CampaignCustomerId}: template is missing or not Approved",
                step.StepNumber, cc.Id);
            // Leave cc exactly where it is - fixing the template lets this step send on the next tick.
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };
        }

        var message = new Message
        {
            CustomerId = customer.Id,
            CampaignCustomerId = cc.Id,
            CampaignStepNumber = step.StepNumber,
            Direction = MessageDirection.Outbound,
            MessageType = MessageType.Template,
            TemplateName = template.WhatsAppTemplateName,
            IdempotencyKey = idempotencyKey,
            Status = MessageStatus.Queued
        };
        _context.Messages.Add(message);
        // Reserve the idempotency key BEFORE calling the external API - see class remarks.
        await _context.SaveChangesAsync(cancellationToken);

        var sent = await AttemptSendAsync(message, step, template, customer, cc, campaign, now, cancellationToken);
        return new SendRunResult(1, sent ? 1 : 0, sent ? 0 : 1, 0);
    }

    private async Task<SendRunResult> RetryOneAsync(Guid messageId, DateTime now, CancellationToken cancellationToken)
    {
        var message = await _context.Messages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null || message.CampaignCustomerId is null || message.CampaignStepNumber is null)
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };

        var cc = await _context.CampaignCustomers.FirstOrDefaultAsync(x => x.Id == message.CampaignCustomerId, cancellationToken);
        var campaign = cc is null
            ? null
            : await _context.Campaigns.Include(c => c.Steps).ThenInclude(s => s.StepMedia).FirstOrDefaultAsync(c => c.Id == cc.CampaignId, cancellationToken);
        var step = campaign?.Steps.FirstOrDefault(s => s.StepNumber == message.CampaignStepNumber);
        var customer = cc is null ? null : await _context.Customers.FirstOrDefaultAsync(c => c.Id == cc.CustomerId, cancellationToken);

        if (cc is null || campaign is null || step is null || customer is null)
        {
            // Something the message pointed at no longer exists - stop retrying rather than looping forever.
            message.Status = MessageStatus.Failed;
            message.FailureReason = "Referenced campaign, step or customer no longer exists.";
            message.NextAttemptAt = null;
            await _context.SaveChangesAsync(cancellationToken);
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };
        }

        // A message can be queued for retry while its campaign is Running and then have that
        // campaign Paused, Stopped or Completed before the retry actually runs - StopAsync only
        // force-completes CampaignCustomers that were AwaitingResponse, it does not touch Messages
        // still waiting on a retry, so without this check a stopped campaign could still have WhatsApp
        // called on its behalf. See the equivalent check in ProcessOneAsync for fresh sends.
        if (campaign.Status != CampaignStatus.Running)
        {
            message.Status = MessageStatus.Failed;
            message.FailureReason = $"Campaign is {campaign.Status}, not Running - retry abandoned.";
            message.NextAttemptAt = null;
            message.AttemptCount = Math.Max(message.AttemptCount, _options.MaxRetryAttempts);
            await _context.SaveChangesAsync(cancellationToken);
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };
        }

        if (customer.OptInStatus != OptInStatus.OptedIn)
        {
            message.Status = MessageStatus.Failed;
            message.FailureReason = "Customer opted out before retry";
            message.NextAttemptAt = null;
            message.AttemptCount = Math.Max(message.AttemptCount, _options.MaxRetryAttempts);

            if (cc.Status is CampaignCustomerStatus.Pending or CampaignCustomerStatus.AwaitingResponse)
            {
                cc.Status = CampaignCustomerStatus.OptedOut;
                cc.StoppedReason = "Customer is not opted in";
                cc.NextFollowUpDueAt = null;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };
        }

        var template = step.MessageTemplateId.HasValue
            ? await _context.MessageTemplates.FirstOrDefaultAsync(t => t.Id == step.MessageTemplateId, cancellationToken)
            : null;
        if (template is null)
        {
            message.Status = MessageStatus.Failed;
            message.FailureReason = "Step no longer has a template assigned.";
            message.NextAttemptAt = null;
            await _context.SaveChangesAsync(cancellationToken);
            return SendRunResult.Empty with { Considered = 1, Skipped = 1 };
        }

        var sent = await AttemptSendAsync(message, step, template, customer, cc, campaign, now, cancellationToken);
        return new SendRunResult(1, sent ? 1 : 0, sent ? 0 : 1, 0);
    }

    /// <summary>Calls WhatsApp and records the outcome on both the message and, on success, the
    /// campaign customer's progress. Shared by a fresh send and a retry of an existing message.</summary>
    private async Task<bool> AttemptSendAsync(
        Message message,
        CampaignStep step,
        MessageTemplate template,
        Customer customer,
        CampaignCustomer cc,
        Campaign campaign,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var (resolvedText, parameterValues) = TemplatePlaceholderResolver.Resolve(step.MessageText, customer);

        var firstMediaId = step.StepMedia.OrderBy(m => m.DisplayOrder).Select(m => (Guid?)m.MediaAssetId).FirstOrDefault();
        var mediaUrl = firstMediaId is null
            ? null
            : await _context.MediaAssets.Where(a => a.Id == firstMediaId).Select(a => a.Url).FirstOrDefaultAsync(cancellationToken);

        var result = await _whatsApp.SendTemplateMessageAsync(
            customer.PhoneNumberE164, template.WhatsAppTemplateName, template.Language, parameterValues, mediaUrl, cancellationToken);

        message.Text = resolvedText;
        message.AttemptCount++;

        if (result.Success)
        {
            message.Status = MessageStatus.Sent;
            message.WhatsAppMessageId = result.WhatsAppMessageId;
            message.SentAt = now;
            message.FailureReason = null;
            message.NextAttemptAt = null;

            cc.CurrentStepNumber = step.StepNumber;
            cc.LastMessageSentAt = now;

            // Same gap-tolerance as the lookup in ProcessOneAsync, and for the same reason: the
            // immediate next-numbered step may have been deactivated without being removed, and an
            // exact match here would mark the customer Completed with active later steps still unsent.
            var nextStep = campaign.Steps
                .Where(s => s.StepNumber > step.StepNumber && s.IsActive)
                .OrderBy(s => s.StepNumber)
                .FirstOrDefault();
            if (nextStep is not null)
            {
                cc.Status = CampaignCustomerStatus.AwaitingResponse;
                cc.NextFollowUpDueAt = now.AddDays(nextStep.DelayDaysAfterPrevious);
            }
            else
            {
                cc.Status = CampaignCustomerStatus.Completed;
                cc.NextFollowUpDueAt = null;
            }
        }
        else
        {
            message.Status = MessageStatus.Failed;
            message.FailureReason = result.ErrorMessage;

            if (message.AttemptCount >= _options.MaxRetryAttempts)
            {
                message.NextAttemptAt = null;
                cc.Status = CampaignCustomerStatus.Failed;
                cc.StoppedReason = $"Step {step.StepNumber} failed after {message.AttemptCount} attempt(s): {result.ErrorMessage}";
                cc.NextFollowUpDueAt = null;
            }
            else
            {
                var backoff = _options.RetryBackoffMinutes;
                var minutes = backoff.Length == 0 ? 5 : backoff[Math.Min(message.AttemptCount - 1, backoff.Length - 1)];
                message.NextAttemptAt = now.AddMinutes(minutes);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return result.Success;
    }

    private static string BuildIdempotencyKey(Guid campaignCustomerId, int stepNumber) => $"{campaignCustomerId}:step{stepNumber}";

    private static SendRunResult Add(SendRunResult total, SendRunResult next) => new(
        total.Considered + next.Considered,
        total.Sent + next.Sent,
        total.Failed + next.Failed,
        total.Skipped + next.Skipped);
}
