using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Messaging;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Consent;

/// <inheritdoc />
public class ConsentInvitationService : IConsentInvitationService
{
    /// <summary>
    /// The scope recorded when a customer answers the invitation. They messaged the business on WhatsApp after being invited to, so the
    /// consent is to be messaged there - and the campaign's own messages are promotional, so Marketing is what lets them through the
    /// consent gate. It is recorded for WhatsApp only, with the inbound message as proof; it says nothing about SMS, email or any other channel.
    /// </summary>
    internal const ConsentScope AnsweredInvitationScope = ConsentScope.Marketing;

    /// <summary>The OptInSource written when an inbound WhatsApp message answers an SMS invitation.</summary>
    internal const string ConsentSource = "WhatsAppReplyToSmsInvitation";

    private readonly IApplicationDbContext _context;
    private readonly IConsentSmsSender _sms;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantContext _tenantContext;
    private readonly ITenantConfigOverrideProvider _tenantConfig;
    private readonly ICampaignSendService _campaignSend;
    private readonly ILogger<ConsentInvitationService> _logger;

    public ConsentInvitationService(
        IApplicationDbContext context,
        IConsentSmsSender sms,
        IDateTimeProvider dateTime,
        ITenantContext tenantContext,
        ITenantConfigOverrideProvider tenantConfig,
        ICampaignSendService campaignSend,
        ILogger<ConsentInvitationService> logger)
    {
        _context = context;
        _sms = sms;
        _dateTime = dateTime;
        _tenantContext = tenantContext;
        _tenantConfig = tenantConfig;
        _campaignSend = campaignSend;
        _logger = logger;
    }

    // ---------------------------------------------------------------------------------------------
    // Sending
    // ---------------------------------------------------------------------------------------------

    public async Task<ConsentInvitationBatchResult> SendAsync(
        Guid campaignId, Guid? executionId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken = default)
    {
        if (customerIds.Count == 0)
            return ConsentInvitationBatchResult.Empty;

        var tenantId = _tenantContext.TenantId ?? throw new InvalidOperationException("Consent invitations are sent on behalf of a tenant.");
        var tenant = await _context.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId, cancellationToken);
        var customers = await _context.Customers.Where(c => customerIds.Contains(c.Id)).ToListAsync(cancellationToken);
        var now = _dateTime.UtcNow;

        var details = new List<string>();
        int sent = 0, failed = 0, skipped = 0;

        // The destination is the tenant's own WhatsApp number. Without it no invitation can say where to chat.
        if (!ConsentSmsMessage.TryBuildLink(tenant.WhatsAppNumber, out var link, out var linkFailure))
        {
            foreach (var customer in customers)
            {
                AddHistory(customer, CustomerHistoryEventTypes.SmsSkipped, null, null, linkFailure, now, campaignId, executionId);
                details.Add($"{customer.PhoneNumberE164}: {linkFailure}");
            }

            await _context.SaveChangesAsync(cancellationToken);
            return new ConsentInvitationBatchResult(0, 0, customers.Count, details);
        }

        var body = ConsentSmsMessage.BuildBody(tenant.Name, link);

        foreach (var customer in customers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var skipReason = SkipReason(customer);
            if (skipReason is null)
                skipReason = ConsentSmsMessage.Preflight(customer.PhoneNumberE164, body, link);

            var key = KeyFor(tenantId, customer.Id, campaignId);
            if (skipReason is null && await _context.ConsentInvitations.AnyAsync(i => i.IdempotencyKey == key, cancellationToken))
                skipReason = "An invitation was already sent for this campaign.";

            if (skipReason is not null)
            {
                skipped++;
                details.Add($"{customer.PhoneNumberE164}: {skipReason}");
                // An already-sent invitation is the idempotent no-op, not an event worth a second line on the history.
                if (!skipReason.StartsWith("An invitation was already sent", StringComparison.Ordinal))
                    AddHistory(customer, CustomerHistoryEventTypes.SmsSkipped, null, null, skipReason, now, campaignId, executionId);
                continue;
            }

            var invitation = new ConsentInvitation
            {
                TenantId = tenantId,
                CustomerId = customer.Id,
                CampaignId = campaignId,
                ExecutionId = executionId,
                IdempotencyKey = key,
                Status = ConsentInvitationStatus.Pending,
                ToPhoneE164 = customer.PhoneNumberE164,
                Body = body,
                WhatsAppLink = link,
                CorrelationId = Guid.NewGuid().ToString("N")
            };

            // Reserved BEFORE the provider is called: if the process dies after the call, the row exists and a rerun skips the customer
            // rather than texting them twice. A racing writer loses on the unique key.
            _context.ConsentInvitations.Add(invitation);
            AddHistory(customer, CustomerHistoryEventTypes.SmsQueued, null, ConsentInvitationStatus.Pending.ToString(),
                "Consent invitation queued.", now, campaignId, executionId, correlationId: invitation.CorrelationId);
            var lostRace = false;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Awaiting is not allowed in a catch filter, so the check happens here: only a lost race on the unique key is swallowed.
                lostRace = await LostRaceAsync(key, cancellationToken);
                if (!lostRace)
                    throw;
            }

            if (lostRace)
            {
                skipped++;
                details.Add($"{customer.PhoneNumberE164}: An invitation was already sent for this campaign.");
                continue;
            }

            var outcome = await AttemptAsync(invitation, customer, now, cancellationToken);
            if (outcome) sent++; else failed++;
        }

        return new ConsentInvitationBatchResult(sent, failed, skipped, details);
    }

    public async Task<int> RetryDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var due = await _context.ConsentInvitations
            .Where(i => i.Status == ConsentInvitationStatus.Failed && i.NextAttemptAt != null && i.NextAttemptAt <= now
                        && i.ConsentRecordedAtUtc == null && i.AttemptCount < ConsentSmsMessage.MaxAttempts)
            .OrderBy(i => i.NextAttemptAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        var tried = 0;
        foreach (var invitation in due)
        {
            var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == invitation.CustomerId, cancellationToken);
            if (customer is null || customer.OptInStatus is OptInStatus.OptedOut or OptInStatus.OptedIn)
            {
                // Consent has been given, or withdrawn, or the customer is gone: no invitation is wanted any more.
                invitation.NextAttemptAt = null;
                invitation.FailureReason = customer is null ? "The customer no longer exists." : $"Not retried: the customer is {customer.OptInStatus}.";
                await _context.SaveChangesAsync(cancellationToken);
                continue;
            }

            await AttemptAsync(invitation, customer, now, cancellationToken);
            tried++;
        }

        return tried;
    }

    /// <summary>One call to the provider for one reserved invitation, and what it did to the invitation and the customer's history. True when accepted.</summary>
    private async Task<bool> AttemptAsync(ConsentInvitation invitation, Customer customer, DateTime now, CancellationToken cancellationToken)
    {
        ConsentSmsSendResult result;
        try
        {
            result = await _sms.SendAsync(new ConsentSmsRequest(invitation.ToPhoneE164, invitation.Body, invitation.IdempotencyKey), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "SMS provider call failed for consent invitation {InvitationId}", invitation.Id);
            result = new ConsentSmsSendResult(false, null, $"The SMS provider could not be reached: {ex.Message}", Transient: true);
        }

        invitation.AttemptCount++;

        if (!result.Accepted)
        {
            MarkFailed(invitation, customer, result.FailureReason ?? "The SMS provider rejected the message.", result.Transient, now);
            await _context.SaveChangesAsync(cancellationToken);
            return false;
        }

        var previous = invitation.Status.ToString();
        invitation.Status = ConsentInvitationStatus.Submitted;
        invitation.ProviderMessageId = result.ProviderMessageId;
        invitation.SubmittedAtUtc = now;
        invitation.FailureReason = null;
        invitation.NextAttemptAt = null;
        AddHistory(customer, CustomerHistoryEventTypes.SmsSubmitted, previous, ConsentInvitationStatus.Submitted.ToString(),
            "Handed to the SMS provider. Submitted is not delivered - delivery is confirmed by the provider's receipt.", now,
            invitation.CampaignId, invitation.ExecutionId, invitation.ProviderMessageId, invitation.CorrelationId);

        if (result.ImmediateReceipt is { } receipt)
            ApplyReceiptTo(invitation, customer, receipt.Status, now, receipt.Reason);

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    private void MarkFailed(ConsentInvitation invitation, Customer customer, string reason, bool transient, DateTime now)
    {
        var previous = invitation.Status.ToString();
        invitation.Status = ConsentInvitationStatus.Failed;
        invitation.FailureReason = reason;
        invitation.NextAttemptAt = transient ? ConsentSmsMessage.NextAttemptAfter(invitation.AttemptCount, now) : null;

        var retryNote = invitation.NextAttemptAt is { } next ? $" It will be tried again after {next:yyyy-MM-dd HH:mm} UTC." : " It will not be tried again.";
        AddHistory(customer, CustomerHistoryEventTypes.SmsFailed, previous, ConsentInvitationStatus.Failed.ToString(),
            $"{reason}{retryNote}", now, invitation.CampaignId, invitation.ExecutionId, invitation.ProviderMessageId, invitation.CorrelationId);
    }

    // ---------------------------------------------------------------------------------------------
    // Delivery receipts
    // ---------------------------------------------------------------------------------------------

    public async Task<bool> ApplyReceiptAsync(
        string providerMessageId, ConsentInvitationStatus status, DateTime atUtc, string? reason, CancellationToken cancellationToken = default)
    {
        if (status is not (ConsentInvitationStatus.Delivered or ConsentInvitationStatus.Failed) || string.IsNullOrWhiteSpace(providerMessageId))
            return false;

        // The receipt endpoint is anonymous and has no tenant yet: the invitation says whose it is. Looked up across tenants on purpose,
        // by the provider's own id, and only then does the tenant become the ambient one for everything written below.
        var invitation = await _context.ConsentInvitations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.ProviderMessageId == providerMessageId, cancellationToken);
        if (invitation is null)
            return false;

        _tenantContext.SetTenant(invitation.TenantId);
        var customer = await _context.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == invitation.CustomerId && c.TenantId == invitation.TenantId, cancellationToken);
        if (customer is null)
            return false;

        var changed = ApplyReceiptTo(invitation, customer, status, atUtc, reason);
        if (changed)
            await _context.SaveChangesAsync(cancellationToken);
        return changed;
    }

    /// <summary>Moves the invitation along a receipt, never backwards and never twice. Delivered is final; a Failed receipt only counts while
    /// the SMS has not been confirmed delivered.</summary>
    private bool ApplyReceiptTo(ConsentInvitation invitation, Customer customer, ConsentInvitationStatus status, DateTime at, string? reason)
    {
        if (invitation.Status == status || invitation.Status == ConsentInvitationStatus.Delivered)
            return false;

        var previous = invitation.Status.ToString();
        if (status == ConsentInvitationStatus.Delivered)
        {
            invitation.Status = ConsentInvitationStatus.Delivered;
            invitation.DeliveredAtUtc = at;
            invitation.FailureReason = null;
            invitation.NextAttemptAt = null;
            AddHistory(customer, CustomerHistoryEventTypes.SmsDelivered, previous, ConsentInvitationStatus.Delivered.ToString(),
                "The provider confirmed the SMS reached the customer's phone.", at, invitation.CampaignId, invitation.ExecutionId,
                invitation.ProviderMessageId, invitation.CorrelationId);
            return true;
        }

        // Reported undelivered: a failure the provider found after accepting it. Retried within the budget like any other.
        MarkFailed(invitation, customer, reason ?? "The provider reported the SMS undelivered.", transient: true, at);
        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // The customer's answer
    // ---------------------------------------------------------------------------------------------

    public async Task<ConsentReplyOutcome> HandleInboundMessageAsync(
        Customer customer, InboundConsentMessage message, CancellationToken cancellationToken = default)
    {
        // An opted-out customer is never turned back into an opted-in one by a reply; that needs a fresh, deliberate opt-in.
        if (customer.OptInStatus == OptInStatus.OptedOut)
            return ConsentReplyOutcome.None;

        // The invitation this message answers: one that was actually sent and has not been answered yet. The customer's reply is what
        // proves it reached them, so Submitted, Delivered and even a Failed-by-receipt one all count.
        var invitation = await _context.ConsentInvitations
            .Where(i => i.CustomerId == customer.Id && i.SubmittedAtUtc != null && i.ConsentRecordedAtUtc == null)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (invitation is null)
            return ConsentReplyOutcome.None;

        var options = await _tenantConfig.GetMessagingOptionsAsync(cancellationToken);
        var windowEnds = message.TimestampUtc.AddHours(options.CustomerServiceWindowHours);
        var correlationId = invitation.CorrelationId;

        AddHistory(customer, CustomerHistoryEventTypes.ConversationStarted, null, null,
            $"The customer started a WhatsApp conversation by messaging the business first. Customer service window open until {windowEnds:yyyy-MM-dd HH:mm} UTC.",
            message.TimestampUtc, invitation.CampaignId, invitation.ExecutionId, message.WhatsAppMessageId, correlationId);

        var consentRecorded = false;
        if (customer.OptInStatus != OptInStatus.OptedIn)
        {
            var previous = customer.OptInStatus.ToString();
            customer.OptInStatus = OptInStatus.OptedIn;
            customer.OptInTimestamp = message.TimestampUtc;
            customer.OptInSource = ConsentSource;
            customer.OptInProof = $"WhatsApp message {message.WhatsAppMessageId}, received in reply to the SMS invitation {invitation.Id}";
            customer.OptInScope = AnsweredInvitationScope;
            customer.OptOutTimestamp = null;
            consentRecorded = true;

            AddHistory(customer, CustomerHistoryEventTypes.ConsentRecorded, previous, OptInStatus.OptedIn.ToString(),
                $"Explicit WhatsApp opt-in. Evidence: inbound message {message.WhatsAppMessageId} at {message.TimestampUtc:yyyy-MM-dd HH:mm:ss} UTC, " +
                $"source {ConsentSource}, scope {AnsweredInvitationScope}, tenant {customer.TenantId}, in reply to SMS invitation {invitation.Id}. " +
                "Covers WhatsApp messaging from this business only.",
                message.TimestampUtc, invitation.CampaignId, invitation.ExecutionId, message.WhatsAppMessageId, correlationId);
        }

        invitation.ConsentRecordedAtUtc = message.TimestampUtc;
        invitation.ConsentMessageId = message.WhatsAppMessageId;
        await _context.SaveChangesAsync(cancellationToken);

        // Consent is on record; now the campaign's first message, once. It goes through the normal pipeline, so every sending rule
        // still applies and a duplicate is impossible (see SendFirstMessageAsync).
        FirstMessageOutcome? firstMessage = null;
        if (invitation.CampaignId is { } campaignId)
        {
            var member = await _context.CampaignCustomers
                .FirstOrDefaultAsync(cc => cc.CampaignId == campaignId && cc.CustomerId == customer.Id, cancellationToken);
            if (member is not null)
            {
                firstMessage = await _campaignSend.SendFirstMessageAsync(member.Id, cancellationToken);
                _logger.LogInformation(
                    "Customer {CustomerId} answered consent invitation {InvitationId}; first message: {Status}",
                    customer.Id, invitation.Id, firstMessage.Status);
            }
        }

        return new ConsentReplyOutcome(true, consentRecorded, firstMessage);
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Why this customer should not get an invitation, or null when they should.</summary>
    private static string? SkipReason(Customer customer) => customer.OptInStatus switch
    {
        OptInStatus.OptedOut => "The customer has opted out.",
        OptInStatus.OptedIn => "The customer has already opted in, so no invitation is needed.",
        _ => null
    };

    private static string KeyFor(Guid tenantId, Guid customerId, Guid campaignId) => $"{tenantId:N}:{customerId:N}:{campaignId:N}";

    private async Task<bool> LostRaceAsync(string key, CancellationToken cancellationToken)
    {
        _context.ResetChangeTracker();
        return await _context.ConsentInvitations.AnyAsync(i => i.IdempotencyKey == key, cancellationToken);
    }

    private void AddHistory(
        Customer customer, string eventType, string? previousStatus, string? newStatus, string? reason, DateTime at,
        Guid? campaignId, Guid? executionId, string? providerReference = null, string? correlationId = null)
    {
        _context.CustomerHistoryEvents.Add(new CustomerHistoryEvent
        {
            TenantId = customer.TenantId,
            CustomerId = customer.Id,
            EventType = eventType,
            OccurredAtUtc = at,
            PreviousStatus = previousStatus,
            NewStatus = newStatus,
            Reason = reason,
            CampaignId = campaignId,
            ExecutionId = executionId,
            CorrelationId = correlationId,
            ProviderReference = providerReference
        });
    }
}
