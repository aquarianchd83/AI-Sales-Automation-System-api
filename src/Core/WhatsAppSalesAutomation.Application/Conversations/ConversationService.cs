using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Conversations;

public class ConversationService : IConversationService
{
    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly IWhatsAppService _whatsApp;
    private readonly MessagingOptions _options;
    private readonly IValidator<ChangeConversationModeRequest> _modeValidator;
    private readonly IValidator<SendConversationMessageRequest> _sendValidator;

    public ConversationService(
        IApplicationDbContext context,
        IDateTimeProvider dateTime,
        IWhatsAppService whatsApp,
        IOptions<MessagingOptions> options,
        IValidator<ChangeConversationModeRequest> modeValidator,
        IValidator<SendConversationMessageRequest> sendValidator)
    {
        _context = context;
        _dateTime = dateTime;
        _whatsApp = whatsApp;
        _options = options.Value;
        _modeValidator = modeValidator;
        _sendValidator = sendValidator;
    }

    public async Task<PagedResult<ConversationDto>> GetPagedAsync(PagedRequest request, string? status = null, CancellationToken cancellationToken = default)
    {
        var query =
            from c in _context.Conversations
            join cust in _context.Customers on c.CustomerId equals cust.Id
            select new { Conversation = c, Customer = cust };

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ConversationStatus>(status, ignoreCase: true, out var parsedStatus))
                throw Invalid("status", $"Status must be one of: {string.Join(", ", Enum.GetNames<ConversationStatus>())}.");

            query = query.Where(x => x.Conversation.Status == parsedStatus);
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
        var rows = await query
            .OrderByDescending(x => x.Conversation.LastMessageAt ?? x.Conversation.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(x => x.Conversation.ToDto(x.Customer)).ToList();

        return new PagedResult<ConversationDto>(items, totalCount, request.Page, request.PageSize);
    }

    public async Task<ConversationDto> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await (
                from c in _context.Conversations
                join cust in _context.Customers on c.CustomerId equals cust.Id
                where c.Id == id
                select new { Conversation = c, Customer = cust })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Conversation), id);

        return row.Conversation.ToDto(row.Customer);
    }

    public async Task<PagedResult<ConversationMessageDto>> GetMessagesAsync(Guid conversationId, PagedRequest request, CancellationToken cancellationToken = default)
    {
        var exists = await _context.Conversations.AnyAsync(c => c.Id == conversationId, cancellationToken);
        if (!exists)
            throw new NotFoundException(nameof(Conversation), conversationId);

        var query = _context.Messages.Where(m => m.ConversationId == conversationId);

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ConversationMessageDto>(rows.Select(m => m.ToDto()).ToList(), totalCount, request.Page, request.PageSize);
    }

    public async Task<ConversationDto> ChangeModeAsync(Guid id, ChangeConversationModeRequest request, CancellationToken cancellationToken = default)
    {
        await _modeValidator.ValidateAndThrowAsync(request, cancellationToken);

        var conversation = await FindOrThrowAsync(id, cancellationToken);
        conversation.Mode = Enum.Parse<ConversationMode>(request.Mode, ignoreCase: true);

        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<ConversationDto> AssignAsync(Guid id, AssignConversationRequest request, CancellationToken cancellationToken = default)
    {
        var conversation = await FindOrThrowAsync(id, cancellationToken);
        conversation.AssignedAgentId = request.AgentId;

        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<ConversationDto> CloseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var conversation = await FindOrThrowAsync(id, cancellationToken);
        conversation.Status = ConversationStatus.Closed;
        conversation.ClosedAt = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<Guid> GetOrCreateActiveConversationIdAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var existingId = await _context.Conversations
            .Where(c => c.CustomerId == customerId && c.Status != ConversationStatus.Closed)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingId.HasValue)
            return existingId.Value;

        var conversation = new Conversation { CustomerId = customerId };
        _context.Conversations.Add(conversation);
        await _context.SaveChangesAsync(cancellationToken);

        return conversation.Id;
    }

    public async Task<ConversationMessageDto> SendMessageAsync(Guid id, SendConversationMessageRequest request, Guid sentByUserId, CancellationToken cancellationToken = default)
    {
        await _sendValidator.ValidateAndThrowAsync(request, cancellationToken);

        var conversation = await FindOrThrowAsync(id, cancellationToken);
        var customer = await _context.Customers.FirstOrDefaultAsync(c => c.Id == conversation.CustomerId, cancellationToken)
            ?? throw new NotFoundException(nameof(Customer), conversation.CustomerId);

        if (customer.OptInStatus == OptInStatus.OptedOut)
            throw new ConflictException("Customer has opted out - no messages can be sent, template or otherwise.");

        string resolvedText;
        string? templateName = null;
        string languageCode = "en";
        IReadOnlyList<string> parameterValues = Array.Empty<string>();

        if (!string.IsNullOrWhiteSpace(request.Text))
        {
            var windowHours = _options.CustomerServiceWindowHours;
            var windowOpen = conversation.LastInboundMessageAt is { } lastInbound &&
                lastInbound.AddHours(windowHours) >= _dateTime.UtcNow;

            if (!windowOpen)
                throw new ConflictException(
                    $"The {windowHours}-hour customer service window is closed (or this customer has never messaged in) - send an approved template instead.");

            resolvedText = request.Text.Trim();
        }
        else
        {
            var template = await _context.MessageTemplates.FirstOrDefaultAsync(t => t.Id == request.MessageTemplateId, cancellationToken)
                ?? throw new NotFoundException(nameof(MessageTemplate), request.MessageTemplateId!.Value);

            if (template.WhatsAppTemplateStatus != WhatsAppTemplateStatus.Approved || !template.IsActive)
                throw new ConflictException($"Template '{template.Name}' is not an active, Approved template.");

            var (text, values) = TemplatePlaceholderResolver.Resolve(template.BodyText, customer);
            resolvedText = text;
            parameterValues = values;
            templateName = template.WhatsAppTemplateName;
            languageCode = template.Language;
        }

        var message = new Message
        {
            CustomerId = customer.Id,
            ConversationId = conversation.Id,
            Direction = MessageDirection.Outbound,
            MessageType = templateName is null ? MessageType.Text : MessageType.Template,
            Text = resolvedText,
            TemplateName = templateName,
            // Not the campaign {CampaignCustomerId}:step{N} scheme - an agent reply has neither. A
            // fresh guid per send is enough: nothing auto-retries an agent-typed message the way the
            // campaign pipeline retries a queued send, so there is no redelivery to stay idempotent
            // against in the first place.
            IdempotencyKey = $"agent:{Guid.NewGuid()}",
            Status = MessageStatus.Queued
        };
        _context.Messages.Add(message);
        await _context.SaveChangesAsync(cancellationToken);

        var result = templateName is null
            ? await _whatsApp.SendTextMessageAsync(customer.PhoneNumberE164, resolvedText, cancellationToken)
            : await _whatsApp.SendTemplateMessageAsync(customer.PhoneNumberE164, templateName, languageCode, parameterValues, cancellationToken: cancellationToken);

        message.AttemptCount++;

        if (result.Success)
        {
            message.Status = MessageStatus.Sent;
            message.WhatsAppMessageId = result.WhatsAppMessageId;
            message.SentAt = _dateTime.UtcNow;
        }
        else
        {
            message.Status = MessageStatus.Failed;
            message.FailureReason = result.ErrorMessage;
        }

        conversation.LastMessageAt = _dateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        // Returned regardless of send success - the agent needs to see a Failed message in the
        // transcript, not just a generic error toast with no record of what was attempted.
        return message.ToDto();
    }

    private async Task<Conversation> FindOrThrowAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.Conversations.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(Conversation), id);

    private static FluentValidation.ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
