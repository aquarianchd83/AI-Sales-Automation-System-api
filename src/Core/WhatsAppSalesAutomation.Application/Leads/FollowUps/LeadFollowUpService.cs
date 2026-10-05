using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Conversations;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Leads;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Leads.FollowUps;

public class LeadFollowUpService : ILeadFollowUpService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IWhatsAppService _whatsApp;
    private readonly IConversationService _conversations;
    private readonly ITenantContext _tenantContext;
    private readonly IQuotaGate _quota;
    private readonly ITenantConfigOverrideProvider _tenantConfig;
    private readonly ITenantTimeZoneProvider _tenantTimeZone;
    private readonly IValidator<ScheduleLeadFollowUpRequest> _scheduleValidator;
    private readonly ILogger<LeadFollowUpService> _logger;
    private readonly IMediaStorageService? _mediaStorage;

    public LeadFollowUpService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IWhatsAppService whatsApp,
        IConversationService conversations,
        ITenantContext tenantContext,
        IQuotaGate quota,
        ITenantConfigOverrideProvider tenantConfig,
        ITenantTimeZoneProvider tenantTimeZone,
        IValidator<ScheduleLeadFollowUpRequest> scheduleValidator,
        ILogger<LeadFollowUpService> logger,
        IMediaStorageService? mediaStorage = null)
    {
        _context = context;
        _dateTime = dateTime;
        _whatsApp = whatsApp;
        _conversations = conversations;
        _tenantContext = tenantContext;
        _quota = quota;
        _tenantConfig = tenantConfig;
        _tenantTimeZone = tenantTimeZone;
        _scheduleValidator = scheduleValidator;
        _logger = logger;
        _mediaStorage = mediaStorage;
    }

    public async Task<PagedResult<LeadFollowUpDto>> GetPagedAsync(
        PagedRequest request, string? status = null, int? dueWithinDays = null, CancellationToken cancellationToken = default)
    {
        var wanted = LeadFollowUpStatus.Scheduled;
        if (!string.IsNullOrWhiteSpace(status) && !Enum.TryParse(status, ignoreCase: true, out wanted))
            throw Invalid("status", $"Status must be one of: {string.Join(", ", Enum.GetNames<LeadFollowUpStatus>())}.");

        var query = BaseQuery().Where(x => x.FollowUp.Status == wanted);

        if (dueWithinDays is { } days && wanted == LeadFollowUpStatus.Scheduled)
        {
            var horizon = _dateTime.UtcNow.AddDays(Math.Max(days, 0));
            query = query.Where(x => x.FollowUp.DueAt <= horizon);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(x =>
                x.Customer.PhoneNumberE164.Contains(search) ||
                (x.Customer.FirstName != null && x.Customer.FirstName.Contains(search)) ||
                (x.Customer.LastName != null && x.Customer.LastName.Contains(search)));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // Scheduled: soonest due first, so the top of the list is what needs attention. History: newest first.
        var ordered = wanted == LeadFollowUpStatus.Scheduled
            ? query.OrderBy(x => x.FollowUp.DueAt)
            : query.OrderByDescending(x => x.FollowUp.UpdatedAt ?? x.FollowUp.CreatedAt);

        var rows = await ordered
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<LeadFollowUpDto>(rows.Select(x => ToDto(x.FollowUp, x.Customer, x.Lead, x.TemplateName)).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<LeadFollowUpSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var in30Days = now.AddDays(30);

        var scheduled = _context.LeadFollowUps.Where(f => f.Status == LeadFollowUpStatus.Scheduled);

        return new LeadFollowUpSummaryDto(
            await scheduled.CountAsync(cancellationToken),
            await scheduled.CountAsync(f => f.DueAt <= now, cancellationToken),
            await scheduled.CountAsync(f => f.DueAt <= in30Days, cancellationToken),
            await _context.LeadFollowUps.CountAsync(f => f.Status == LeadFollowUpStatus.Sent, cancellationToken));
    }

    public async Task<IReadOnlyList<LeadFollowUpDto>> GetForLeadAsync(Guid leadId, CancellationToken cancellationToken = default)
    {
        if (!await _context.Leads.AnyAsync(l => l.Id == leadId, cancellationToken))
            throw new NotFoundException(nameof(Lead), leadId);

        var rows = await BaseQuery()
            .Where(x => x.FollowUp.LeadId == leadId)
            .OrderByDescending(x => x.FollowUp.CreatedAt)
            .ToListAsync(cancellationToken);

        return rows.Select(x => ToDto(x.FollowUp, x.Customer, x.Lead, x.TemplateName)).ToList();
    }

    public async Task<LeadFollowUpDto> ScheduleAsync(Guid leadId, ScheduleLeadFollowUpRequest request, Guid scheduledByUserId, CancellationToken cancellationToken = default)
    {
        await _scheduleValidator.ValidateAndThrowAsync(request, cancellationToken);

        var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken)
            ?? throw new NotFoundException(nameof(Lead), leadId);

        if (lead.Stage is LeadStage.Won or LeadStage.Lost)
            throw new ConflictException($"This lead is {lead.Stage}; there is nothing left to follow up on.");

        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == lead.CustomerId, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), lead.CustomerId);

        if (customer.OptInStatus == OptInStatus.OptedOut)
            throw new ConflictException("This customer has opted out of messages, so a follow-up cannot be scheduled.");
        if (customer.OptInStatus != OptInStatus.OptedIn)
            throw new ConflictException("This customer has not opted in to messages yet, so a follow-up cannot be scheduled.");

        var template = await _context.MessageTemplates.FirstOrDefaultAsync(t => t.Id == request.MessageTemplateId, cancellationToken)
            ?? throw Invalid(nameof(request.MessageTemplateId), "Message template not found.");
        if (!IsUsable(template))
            throw Invalid(nameof(request.MessageTemplateId), "Choose an approved, active message template - WhatsApp only allows those for a message the business starts.");

        var alreadySent = await _context.LeadFollowUps.CountAsync(f => f.LeadId == leadId && f.Status == LeadFollowUpStatus.Sent, cancellationToken);
        if (alreadySent >= LeadFollowUpPolicy.MaxSentPerLead)
        {
            throw new ConflictException(
                $"This customer has already been followed up {alreadySent} times. Another reminder would only irritate - " +
                "if they get back in touch, the lead can be worked from the conversation instead.");
        }

        var now = _dateTime.UtcNow;
        DateTime dueAt;
        if (request.Months is { } months)
        {
            dueAt = now.AddMonths(months);
        }
        else
        {
            dueAt = DateTime.SpecifyKind(request.DueAt!.Value.ToUniversalTime(), DateTimeKind.Utc);
            if (dueAt < now.AddDays(LeadFollowUpPolicy.MinDaysAhead))
                throw Invalid(nameof(request.DueAt), $"Pick a date at least {LeadFollowUpPolicy.MinDaysAhead} days from now.");
            if (dueAt > now.AddMonths(LeadFollowUpPolicy.MaxMonths))
                throw Invalid(nameof(request.DueAt), $"Pick a date within {LeadFollowUpPolicy.MaxMonths} months.");
        }

        // The earlier reminder is cancelled and saved BEFORE the new one is added: the one-Scheduled-per-lead
        // unique index would reject a single SaveChanges in which EF happens to insert before it updates.
        var current = await _context.LeadFollowUps.FirstOrDefaultAsync(f => f.LeadId == leadId && f.Status == LeadFollowUpStatus.Scheduled, cancellationToken);
        if (current is not null)
        {
            current.Status = LeadFollowUpStatus.Cancelled;
            current.OutcomeNote = "Replaced by a newer follow-up.";
            await _context.SaveChangesAsync(cancellationToken);
        }

        var followUp = new LeadFollowUp
        {
            LeadId = leadId,
            CustomerId = customer.Id,
            DueAt = dueAt,
            IntervalMonths = request.Months,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            MessageTemplateId = template.Id,
            FollowUpNumber = alreadySent + 1,
            ScheduledBy = scheduledByUserId
        };
        _context.LeadFollowUps.Add(followUp);

        var when = request.Months is { } m ? $"in {m} month{(m == 1 ? "" : "s")}" : $"on {dueAt:yyyy-MM-dd}";
        AddActivity(lead, LeadActivityType.FollowUpScheduled, null, dueAt.ToString("yyyy-MM-dd"),
            $"Follow-up scheduled {when}" + (followUp.Reason is null ? "." : $": {followUp.Reason}"), scheduledByUserId);
        lead.LastActivityAt = now;

        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(followUp, customer, lead, template.Name);
    }

    public async Task<LeadFollowUpDto> CancelAsync(Guid id, Guid cancelledByUserId, CancellationToken cancellationToken = default)
    {
        var followUp = await FindOrThrowAsync(id, cancellationToken);
        if (followUp.Status is not (LeadFollowUpStatus.Scheduled or LeadFollowUpStatus.Failed))
            throw new ConflictException($"This follow-up is already {followUp.Status}; there is nothing to cancel.");

        var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == followUp.LeadId, cancellationToken);

        followUp.Status = LeadFollowUpStatus.Cancelled;
        followUp.OutcomeNote = "Cancelled by a team member.";
        if (lead is not null)
        {
            AddActivity(lead, LeadActivityType.FollowUpCancelled, followUp.DueAt.ToString("yyyy-MM-dd"), null, "Follow-up cancelled.", cancelledByUserId);
            lead.LastActivityAt = _dateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);

        return await GetDtoAsync(id, cancellationToken);
    }

    public async Task<LeadFollowUpDto> SendNowAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var followUp = await FindOrThrowAsync(id, cancellationToken);
        if (followUp.Status is not (LeadFollowUpStatus.Scheduled or LeadFollowUpStatus.Failed))
            throw new ConflictException($"This follow-up is already {followUp.Status}; it cannot be sent again.");

        var options = await _tenantConfig.GetMessagingOptionsAsync(cancellationToken);
        var outcome = await TrySendAsync(followUp.Id, byPerson: true, _dateTime.UtcNow, options, cancellationToken);

        // A person asked for this one send, so "nothing happened" is an answer to give them, not to hide.
        if (outcome == SendOutcome.Skipped)
            throw new ConflictException("The follow-up could not be sent right now - check that its template is still approved, that the customer has opted in and that the account has WhatsApp quota left.");

        // Already saved as Cancelled by the re-check (opted out, lead closed): say why rather than return quietly.
        if (outcome == SendOutcome.Cancelled)
            throw new ConflictException($"The follow-up was cancelled instead of sent: {(await RowAsync(id, cancellationToken)).OutcomeNote}");

        return await GetDtoAsync(id, cancellationToken);
    }

    public async Task<LeadFollowUpRunResult> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;

        // Checked once for the whole pass: it is the tenant's clock, not the follow-up's. Outside the window
        // nothing is touched - the due rows simply wait for the next tick inside it.
        var localHour = (await _tenantTimeZone.GetLocalNowAsync(cancellationToken)).Hour;
        if (localHour < LeadFollowUpPolicy.SendWindowStartHour || localHour >= LeadFollowUpPolicy.SendWindowEndHour)
            return LeadFollowUpRunResult.Empty;

        var options = await _tenantConfig.GetMessagingOptionsAsync(cancellationToken);

        var dueIds = await _context.LeadFollowUps
            .Where(f => f.Status == LeadFollowUpStatus.Scheduled && f.DueAt <= now)
            .OrderBy(f => f.DueAt)
            .Take(options.MaxSendsPerRun)
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);

        var result = LeadFollowUpRunResult.Empty with { Considered = dueIds.Count };
        foreach (var id in dueIds)
        {
            var outcome = await TrySendAsync(id, byPerson: false, now, options, cancellationToken);
            result = outcome switch
            {
                SendOutcome.Sent => result with { Sent = result.Sent + 1 },
                SendOutcome.Failed => result with { Failed = result.Failed + 1 },
                SendOutcome.Deferred => result with { Deferred = result.Deferred + 1 },
                SendOutcome.Cancelled => result with { Cancelled = result.Cancelled + 1 },
                _ => result with { Skipped = result.Skipped + 1 }
            };
        }

        return result;
    }

    private enum SendOutcome { Sent, Failed, Skipped, Deferred, Cancelled }

    /// <summary>Re-checks everything that could have changed since the follow-up was scheduled, then sends. Checks
    /// that protect the customer (opt-in, a lead already closed, the customer having written in, a message very
    /// recently sent) end the follow-up or push it back; checks that are about the account (template, quota)
    /// leave it Scheduled to try again once fixed.</summary>
    private async Task<SendOutcome> TrySendAsync(Guid followUpId, bool byPerson, DateTime now, MessagingOptions options, CancellationToken cancellationToken)
    {
        var followUp = await _context.LeadFollowUps.FirstOrDefaultAsync(f => f.Id == followUpId, cancellationToken);
        if (followUp is null || followUp.Status is not (LeadFollowUpStatus.Scheduled or LeadFollowUpStatus.Failed))
            return SendOutcome.Skipped;

        var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == followUp.LeadId, cancellationToken);
        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == followUp.CustomerId, cancellationToken);

        if (customer is null || customer.OptInStatus == OptInStatus.OptedOut)
            return await EndAsync(followUp, lead, LeadFollowUpStatus.Cancelled, "Customer has opted out.", cancellationToken);

        // Never opted in (or opt-in withdrawn back to pending): not a message to send, but not final either.
        if (customer.OptInStatus != OptInStatus.OptedIn)
            return SendOutcome.Skipped;

        if (lead is null || lead.Stage is LeadStage.Won or LeadStage.Lost)
            return await EndAsync(followUp, lead, LeadFollowUpStatus.Cancelled, $"Lead is {lead?.Stage.ToString() ?? "gone"}.", cancellationToken);

        if (!byPerson)
        {
            // They came back on their own after being parked: the conversation is live again and a scripted
            // "just checking in" on top of it is exactly the irritation this feature exists to avoid.
            var wroteIn = await _context.Conversations.AnyAsync(
                c => c.CustomerId == customer.Id && c.LastInboundMessageAt != null && c.LastInboundMessageAt > followUp.CreatedAt, cancellationToken);
            if (wroteIn)
                return await EndAsync(followUp, lead, LeadFollowUpStatus.Skipped, "Customer got in touch after this was scheduled, so no reminder was needed.", cancellationToken);

            var quietSince = now.AddDays(-LeadFollowUpPolicy.QuietDaysAfterAnyMessage);
            var lastSent = await _context.Messages
                .Where(m => m.CustomerId == customer.Id && m.Direction == MessageDirection.Outbound && m.SentAt != null && m.SentAt > quietSince)
                .MaxAsync(m => m.SentAt, cancellationToken);
            if (lastSent is { } last)
            {
                followUp.DueAt = last.AddDays(LeadFollowUpPolicy.QuietDaysAfterAnyMessage);
                await _context.SaveChangesAsync(cancellationToken);
                return SendOutcome.Deferred;
            }
        }

        var template = await _context.MessageTemplates.FirstOrDefaultAsync(t => t.Id == followUp.MessageTemplateId, cancellationToken);
        if (template is null || !IsUsable(template))
        {
            _logger.LogWarning("Lead follow-up {FollowUpId} not sent: its template is missing or not Approved", followUp.Id);
            return SendOutcome.Skipped;
        }

        var attempt = followUp.AttemptCount + 1;
        var idempotencyKey = $"leadfollowup:{followUp.Id}:{attempt}";

        if (_tenantContext.TenantId is { } tenantId &&
            !await _quota.TryConsumeWhatsAppTemplateAsync(tenantId, template.Category, $"wa:{idempotencyKey}", idempotencyKey, cancellationToken))
        {
            return SendOutcome.Skipped;
        }

        var conversationId = await _conversations.GetOrCreateActiveConversationIdAsync(customer.Id, cancellationToken);

        var message = new Message
        {
            CustomerId = customer.Id,
            ConversationId = conversationId,
            Direction = MessageDirection.Outbound,
            MessageType = MessageType.Template,
            TemplateName = template.WhatsAppTemplateName,
            IdempotencyKey = idempotencyKey,
            Status = MessageStatus.Queued
        };
        _context.Messages.Add(message);
        followUp.AttemptCount = attempt;
        // The key is reserved before WhatsApp is called - the same rule the campaign sender follows.
        await _context.SaveChangesAsync(cancellationToken);

        var (resolvedText, parameterValues) = TemplatePlaceholderResolver.Resolve(template.BodyText, customer);
        var mediaUrl = await TemplateHeaderImage.ResolveUrlAsync(_context, template, cancellationToken, _mediaStorage);

        var result = await _whatsApp.SendTemplateMessageAsync(
            customer.PhoneNumberE164, template.WhatsAppTemplateName, template.Language, parameterValues, mediaUrl, cancellationToken);

        message.Text = resolvedText;
        message.AttemptCount = 1;

        if (result.Success)
        {
            message.Status = MessageStatus.Sent;
            message.WhatsAppMessageId = result.WhatsAppMessageId;
            message.SentAt = now;

            var conversation = await _context.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
            if (conversation is not null)
                conversation.LastMessageAt = now;

            followUp.Status = LeadFollowUpStatus.Sent;
            followUp.SentAt = now;
            followUp.MessageId = message.Id;
            followUp.OutcomeNote = null;

            if (lead is not null)
            {
                AddActivity(lead, LeadActivityType.FollowUpSent, null, null, $"Follow-up #{followUp.FollowUpNumber} sent.", null);
                lead.LastActivityAt = now;
            }
        }
        else
        {
            message.Status = MessageStatus.Failed;
            message.FailureReason = result.ErrorMessage;
            // This message does not belong to a campaign, so the campaign retry job has nothing to retry it
            // against. Marking its attempts as spent keeps that job from picking it up and rewriting it.
            message.AttemptCount = Math.Max(1, options.MaxRetryAttempts);

            followUp.Status = LeadFollowUpStatus.Failed;
            followUp.OutcomeNote = Truncate(result.ErrorMessage ?? "WhatsApp did not accept the message.", 500);

            await _quota.ReleaseAsync(message.TenantId, $"wa:{idempotencyKey}", cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);
        return result.Success ? SendOutcome.Sent : SendOutcome.Failed;
    }

    private async Task<SendOutcome> EndAsync(LeadFollowUp followUp, Lead? lead, LeadFollowUpStatus status, string note, CancellationToken cancellationToken)
    {
        followUp.Status = status;
        followUp.OutcomeNote = note;

        if (lead is not null)
            AddActivity(lead, LeadActivityType.FollowUpCancelled, followUp.DueAt.ToString("yyyy-MM-dd"), null, $"Follow-up not sent: {note}", null);

        await _context.SaveChangesAsync(cancellationToken);
        return status == LeadFollowUpStatus.Cancelled ? SendOutcome.Cancelled : SendOutcome.Skipped;
    }

    private static bool IsUsable(MessageTemplate template) =>
        template.IsActive && template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved;

    private void AddActivity(Lead lead, LeadActivityType type, string? oldValue, string? newValue, string note, Guid? createdBy) =>
        _context.LeadActivities.Add(new LeadActivity
        {
            LeadId = lead.Id,
            ActivityType = type,
            OldValue = oldValue,
            NewValue = newValue,
            Note = note,
            CreatedBy = createdBy
        });

    // Anonymous-type projection, not a custom record: EF cannot translate further Where/OrderBy on a record
    // projection - see LeadService.GetPagedAsync and HandoffService.BaseQuery.
    private IQueryable<FollowUpRow> BaseQuery() =>
        from f in _context.LeadFollowUps
        join l in _context.Leads on f.LeadId equals l.Id
        join c in _context.Customers on f.CustomerId equals c.Id
        join t in _context.MessageTemplates on f.MessageTemplateId equals t.Id into templates
        from t in templates.DefaultIfEmpty()
        select new FollowUpRow { FollowUp = f, Lead = l, Customer = c, TemplateName = t == null ? null : t.Name };

    private sealed class FollowUpRow
    {
        public LeadFollowUp FollowUp { get; set; } = null!;
        public Lead Lead { get; set; } = null!;
        public Customer Customer { get; set; } = null!;
        public string? TemplateName { get; set; }
    }

    private Task<LeadFollowUp> RowAsync(Guid id, CancellationToken cancellationToken) =>
        _context.LeadFollowUps.FirstAsync(f => f.Id == id, cancellationToken);

    private async Task<LeadFollowUp> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.LeadFollowUps.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(LeadFollowUp), id);

    private async Task<LeadFollowUpDto> GetDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await BaseQuery().FirstOrDefaultAsync(x => x.FollowUp.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(LeadFollowUp), id);
        return ToDto(row.FollowUp, row.Customer, row.Lead, row.TemplateName);
    }

    private static LeadFollowUpDto ToDto(LeadFollowUp f, Customer customer, Lead lead, string? templateName) => new(
        f.Id, f.LeadId, f.CustomerId, customer.FullName, customer.PhoneNumberE164, lead.Stage.ToString(), f.Status.ToString(),
        f.DueAt, f.IntervalMonths, f.Reason, f.MessageTemplateId, templateName, f.FollowUpNumber, f.SentAt, f.OutcomeNote, f.CreatedAt);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
