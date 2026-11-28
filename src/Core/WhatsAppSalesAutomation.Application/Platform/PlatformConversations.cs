using System.Text.RegularExpressions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <param name="WindowOpen">True while free text can be sent: the other side wrote in the last 24 hours.</param>
/// <param name="WindowClosesAtUtc">When the current 24-hour window ends; null when it is not open.</param>
public record PlatformConversationDto(
    Guid Id,
    Guid? TenantId,
    string? TenantName,
    string PhoneE164,
    string? ContactName,
    DateTime? LastMessageAtUtc,
    DateTime? LastInboundAtUtc,
    string? LastMessagePreview,
    int UnreadCount,
    bool WindowOpen,
    DateTime? WindowClosesAtUtc);

public record PlatformConversationMessageDto(
    Guid Id,
    MessageDirection Direction,
    string? Body,
    string MessageType,
    string? TemplateName,
    MessageStatus Status,
    string? ErrorMessage,
    string? SentByEmail,
    DateTime SentAtUtc);

public record PlatformConversationDetailDto(PlatformConversationDto Conversation, IReadOnlyList<PlatformConversationMessageDto> Messages, bool HasOlder);

/// <summary>One number a tenant can be reached on, and where it came from ("Billing alerts", "Owner", ...).</summary>
public record PlatformTenantPhoneDto(string Label, string PhoneE164);

/// <summary>A tenant the admin can start a chat with: its numbers, and the chat that already exists, if any.</summary>
public record PlatformConversationContactDto(Guid TenantId, string TenantName, IReadOnlyList<PlatformTenantPhoneDto> Phones, Guid? ConversationId);

/// <summary>An approved platform template the admin may use to (re)open a chat. Only <c>{{TenantName}}</c>, <c>{{Title}}</c> and <c>{{Message}}</c> are filled.</summary>
public record PlatformConversationTemplateDto(Guid Id, string Name, string WhatsAppTemplateName, string Language, string BodyText, bool UsesTitle, bool UsesMessage, bool IsDefault);

/// <summary>Either <paramref name="TenantId"/> (the number is picked from the tenant's own numbers when <paramref name="Phone"/> is empty) or just a number.</summary>
public record StartPlatformConversationRequest(Guid? TenantId, string? Phone);

/// <summary>Free text (<paramref name="Text"/>) or a template (<paramref name="TemplateId"/> with <paramref name="Title"/>/<paramref name="Message"/> for its values).</summary>
public record SendPlatformConversationMessageRequest(string? Text, Guid? TemplateId, string? Title, string? Message);

/// <summary>How to get a tenant to write first, so the 24-hour window opens and free text works without a template: a wa.me link to the
/// platform number (with a ready "Hi" text) and the tenant's email to send it to. Either may be null when it is not known.</summary>
public record PlatformConversationInviteDto(string? PlatformNumber, string? WhatsAppLink, string? Email, string? EmailSource, string PrefilledText);

/// <param name="Note">An optional line from the admin, put at the top of the email.</param>
public record SendPlatformConversationInviteRequest(string? Note);

/// <summary>Pushes "this chat changed" to every open Platform Admin Console, so the conversations page refreshes without polling. Never throws.</summary>
public interface IPlatformConversationBroadcaster
{
    Task ConversationUpdatedAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The Platform Admin Console's Tenant conversations page: WhatsApp chats between the PLATFORM's own number (the one tenant notices go from)
/// and tenants. The admin writes free text while the tenant's 24-hour window is open and an approved platform template otherwise; the tenant's
/// replies arrive through the same Meta webhook every number uses (WebhooksController routes the platform number here).
/// </summary>
public interface IPlatformConversationService
{
    Task<IReadOnlyList<PlatformConversationDto>> ListAsync(string? search, CancellationToken cancellationToken = default);

    /// <summary>The newest <paramref name="take"/> messages (older than <paramref name="beforeUtc"/> when given), oldest first. Marks the chat read.</summary>
    Task<PlatformConversationDetailDto> GetAsync(Guid id, DateTime? beforeUtc, int take, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlatformConversationContactDto>> GetContactsAsync(string? search, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlatformConversationTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the chat for a tenant (or number), or returns the one that already exists for that number.</summary>
    Task<PlatformConversationDto> StartAsync(StartPlatformConversationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends from the platform number. A message WhatsApp refused is still returned (status Failed, with the reason) so the admin sees it.</summary>
    Task<PlatformConversationMessageDto> SendAsync(Guid id, SendPlatformConversationMessageRequest request, Guid? userId, string? userEmail, CancellationToken cancellationToken = default);

    Task<PlatformConversationInviteDto> GetInviteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Emails the tenant the wa.me link, asking them to message the platform number. Recorded in the chat as an "email" message.</summary>
    Task<PlatformConversationMessageDto> SendInviteEmailAsync(Guid id, SendPlatformConversationInviteRequest request, Guid? userId, string? userEmail, CancellationToken cancellationToken = default);

    /// <summary>Records what Meta delivered to the platform number: replies become inbound messages, statuses update the admin's sent messages.</summary>
    Task RecordWebhookAsync(WhatsAppWebhookParseResult parsed, CancellationToken cancellationToken = default);
}

public class PlatformConversationService : IPlatformConversationService
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public const int MaxTextLength = 4000;

    private static readonly Regex TokenPattern = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    private readonly IApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPlatformWhatsAppSender _sender;
    private readonly IDateTimeProvider _dateTime;
    private readonly ILogger<PlatformConversationService> _logger;
    private readonly IPlatformConversationBroadcaster? _broadcaster;
    private readonly IMediaStorageService? _storage;
    private readonly IEmailSender? _email;
    private readonly IAppSettingsStore? _settings;

    public const string InviteText = "Hi AiSaarthi team";

    public PlatformConversationService(
        IApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IPlatformWhatsAppSender sender,
        IDateTimeProvider dateTime,
        ILogger<PlatformConversationService> logger,
        IPlatformConversationBroadcaster? broadcaster = null,
        IMediaStorageService? storage = null,
        IEmailSender? email = null,
        IAppSettingsStore? settings = null)
    {
        _context = context;
        _userManager = userManager;
        _sender = sender;
        _dateTime = dateTime;
        _logger = logger;
        _broadcaster = broadcaster;
        _storage = storage;
        _email = email;
        _settings = settings;
    }

    public async Task<IReadOnlyList<PlatformConversationDto>> ListAsync(string? search, CancellationToken cancellationToken = default)
    {
        var query = _context.PlatformConversations.AsNoTracking();
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var tenantIds = await _context.Tenants.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Name.Contains(term)).Select(t => t.Id).ToListAsync(cancellationToken);
            query = query.Where(c => c.PhoneE164.Contains(term) || (c.ContactName != null && c.ContactName.Contains(term))
                                     || (c.TenantId != null && tenantIds.Contains(c.TenantId.Value)));
        }

        var rows = await query.OrderByDescending(c => c.LastMessageAtUtc ?? c.CreatedAt).Take(200).ToListAsync(cancellationToken);
        var names = await TenantNamesAsync(rows.Where(r => r.TenantId != null).Select(r => r.TenantId!.Value), cancellationToken);
        return rows.Select(r => ToDto(r, names)).ToList();
    }

    public async Task<PlatformConversationDetailDto> GetAsync(Guid id, DateTime? beforeUtc, int take, CancellationToken cancellationToken = default)
    {
        var conversation = await _context.PlatformConversations.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(PlatformConversation), id);

        take = Math.Clamp(take, 1, 200);
        var query = _context.PlatformConversationMessages.AsNoTracking().Where(m => m.ConversationId == id);
        if (beforeUtc is { } before)
            query = query.Where(m => m.SentAtUtc < before);

        var page = await query.OrderByDescending(m => m.SentAtUtc).Take(take + 1).ToListAsync(cancellationToken);
        var hasOlder = page.Count > take;
        var kept = page.Take(take).ToList();

        // The next page asks for messages strictly older than the oldest one here, so a page must never end partway through a group of
        // messages with the same time (Meta stamps whole seconds): take the whole group, and only then say whether anything older remains.
        if (hasOlder && page[take].SentAtUtc == kept[^1].SentAtUtc)
        {
            var boundary = kept[^1].SentAtUtc;
            var keptIds = kept.Select(m => m.Id).ToHashSet();
            kept.AddRange((await query.Where(m => m.SentAtUtc == boundary).ToListAsync(cancellationToken)).Where(m => !keptIds.Contains(m.Id)));
            hasOlder = await query.AnyAsync(m => m.SentAtUtc < boundary, cancellationToken);
        }

        var messages = kept.OrderBy(m => m.SentAtUtc).ThenBy(m => m.CreatedAt).Select(ToDto).ToList();

        if (beforeUtc is null && conversation.UnreadCount != 0)
        {
            conversation.UnreadCount = 0;
            await _context.SaveChangesAsync(cancellationToken);
        }

        var names = await TenantNamesAsync(conversation.TenantId is { } t ? new[] { t } : Array.Empty<Guid>(), cancellationToken);
        return new PlatformConversationDetailDto(ToDto(conversation, names), messages, hasOlder);
    }

    public async Task<IReadOnlyList<PlatformConversationContactDto>> GetContactsAsync(string? search, CancellationToken cancellationToken = default)
    {
        var query = _context.Tenants.IgnoreQueryFilters().AsNoTracking();
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
            query = query.Where(t => t.Name.Contains(term) || (t.BillingAlertPhoneE164 != null && t.BillingAlertPhoneE164.Contains(term))
                                     || (t.WhatsAppNumber != null && t.WhatsAppNumber.Contains(term)) || (t.SupportPhone != null && t.SupportPhone.Contains(term)));

        var tenants = await query.OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, t.BillingAlertPhoneE164, t.WhatsAppNumber, t.SupportPhone, t.OwnerUserId })
            .Take(50).ToListAsync(cancellationToken);

        var ownerIds = tenants.Where(t => t.OwnerUserId != null).Select(t => t.OwnerUserId!.Value).ToList();
        var ownerPhones = ownerIds.Count == 0
            ? new Dictionary<Guid, string?>()
            : await _userManager.Users.IgnoreQueryFilters().AsNoTracking().Where(u => ownerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.PhoneNumber, cancellationToken);

        var tenantIds = tenants.Select(t => t.Id).ToList();
        var existing = await _context.PlatformConversations.AsNoTracking()
            .Where(c => c.TenantId != null && tenantIds.Contains(c.TenantId.Value))
            .OrderByDescending(c => c.LastMessageAtUtc)
            .Select(c => new { c.TenantId, c.Id })
            .ToListAsync(cancellationToken);

        return tenants.Select(t => new PlatformConversationContactDto(
            t.Id, t.Name,
            PhonesOf(t.BillingAlertPhoneE164, t.WhatsAppNumber, t.SupportPhone, t.OwnerUserId is { } o ? ownerPhones.GetValueOrDefault(o) : null),
            existing.FirstOrDefault(e => e.TenantId == t.Id)?.Id)).ToList();
    }

    public async Task<IReadOnlyList<PlatformConversationTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var approved = await _context.PlatformMessageTemplates.AsNoTracking()
            .Where(t => t.IsActive && t.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved)
            .ToListAsync(cancellationToken);

        var defaultKey = TenantNotificationKind.AdminConversation.ToString();
        return approved
            .OrderByDescending(t => t.EventKey == defaultKey)
            .ThenBy(t => t.Name)
            .Select(t =>
            {
                var tokens = TemplatePlaceholderResolver.ExtractTokens(t.BodyText);
                return new PlatformConversationTemplateDto(
                    t.Id, t.Name, t.WhatsAppTemplateName, t.Language, t.BodyText,
                    tokens.Contains(PlatformTemplateCatalog.TitleToken, StringComparer.OrdinalIgnoreCase),
                    tokens.Contains(PlatformTemplateCatalog.MessageToken, StringComparer.OrdinalIgnoreCase),
                    t.EventKey == defaultKey);
            })
            .ToList();
    }

    public async Task<PlatformConversationDto> StartAsync(StartPlatformConversationRequest request, CancellationToken cancellationToken = default)
    {
        string? phone = null;
        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            if (!PhoneNumberNormalizer.TryNormalize(request.Phone, out var normalized))
                throw Invalid(nameof(request.Phone), "Enter the WhatsApp number with the country code, for example +919876543210.");
            phone = normalized;
        }

        Guid? tenantId = request.TenantId;
        if (tenantId is { } tid)
        {
            var tenant = await _context.Tenants.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == tid)
                .Select(t => new { t.Id, t.BillingAlertPhoneE164, t.WhatsAppNumber, t.SupportPhone, t.OwnerUserId })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Tenant", tid);

            if (phone is null)
            {
                var ownerPhone = tenant.OwnerUserId is { } o
                    ? await _userManager.Users.IgnoreQueryFilters().Where(u => u.Id == o).Select(u => u.PhoneNumber).FirstOrDefaultAsync(cancellationToken)
                    : null;
                phone = PhonesOf(tenant.BillingAlertPhoneE164, tenant.WhatsAppNumber, tenant.SupportPhone, ownerPhone).FirstOrDefault()?.PhoneE164
                    ?? throw Invalid(nameof(request.Phone), "This tenant has no WhatsApp number on file. Enter the number to message.");
            }
        }
        else if (phone is null)
        {
            throw Invalid(nameof(request.TenantId), "Pick a tenant or enter a WhatsApp number.");
        }

        var conversation = await _context.PlatformConversations.FirstOrDefaultAsync(c => c.PhoneE164 == phone, cancellationToken);
        if (conversation is null)
        {
            conversation = new PlatformConversation { PhoneE164 = phone, TenantId = tenantId ?? await FindTenantByPhoneAsync(phone, cancellationToken) };
            _context.PlatformConversations.Add(conversation);
            await _context.SaveChangesAsync(cancellationToken);
        }
        else if (tenantId is not null && conversation.TenantId != tenantId)
        {
            conversation.TenantId = tenantId;
            await _context.SaveChangesAsync(cancellationToken);
        }

        var names = await TenantNamesAsync(conversation.TenantId is { } t ? new[] { t } : Array.Empty<Guid>(), cancellationToken);
        return ToDto(conversation, names);
    }

    public async Task<PlatformConversationMessageDto> SendAsync(
        Guid id, SendPlatformConversationMessageRequest request, Guid? userId, string? userEmail, CancellationToken cancellationToken = default)
    {
        var conversation = await _context.PlatformConversations.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(PlatformConversation), id);

        var now = _dateTime.UtcNow;
        var message = new PlatformConversationMessage
        {
            ConversationId = conversation.Id,
            Direction = MessageDirection.Outbound,
            Status = MessageStatus.Queued,
            SentByUserId = userId,
            SentByEmail = userEmail,
            SentAtUtc = now
        };

        DeliveryResult result;
        if (request.TemplateId is { } templateId)
        {
            var template = await _context.PlatformMessageTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, cancellationToken)
                ?? throw new NotFoundException(nameof(PlatformMessageTemplate), templateId);
            if (!template.IsActive || template.WhatsAppTemplateStatus != WhatsAppTemplateStatus.Approved)
                throw Invalid(nameof(request.TemplateId), $"The template \"{template.Name}\" is {(template.IsActive ? template.WhatsAppTemplateStatus.ToString() : "switched off")}, so WhatsApp would not deliver it. Pick an Approved template.");

            var tenantName = conversation.TenantId is { } tid
                ? await _context.Tenants.IgnoreQueryFilters().Where(t => t.Id == tid).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken)
                : null;
            var nameValue = tenantName ?? conversation.ContactName ?? "there";
            var title = request.Title?.Trim() ?? string.Empty;
            var text = request.Message?.Trim() ?? string.Empty;
            if (text.Length > PlatformTemplateCatalog.MaxValueLength || title.Length > PlatformTemplateCatalog.MaxValueLength)
                throw Invalid(nameof(request.Message), $"Keep the template's message under {PlatformTemplateCatalog.MaxValueLength} characters.");

            var parameters = PlatformNoticeTemplates.BuildParameters(template.BodyText, nameValue, title, text);
            var mediaUrl = await new PlatformNoticeTemplates(_context, _storage).HeaderUrlAsync(template, cancellationToken);

            message.TemplateName = template.WhatsAppTemplateName;
            message.Body = Render(template.BodyText, nameValue, title, text);
            _context.PlatformConversationMessages.Add(message);
            await _context.SaveChangesAsync(cancellationToken);

            result = await _sender.SendTemplateAsync(conversation.PhoneE164, template.WhatsAppTemplateName, template.Language, parameters, cancellationToken, mediaUrl);
        }
        else
        {
            var text = request.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
                throw Invalid(nameof(request.Text), "Type a message.");
            if (text.Length > MaxTextLength)
                throw Invalid(nameof(request.Text), $"A WhatsApp message can be at most {MaxTextLength} characters.");
            if (!IsWindowOpen(conversation, now))
                throw Invalid(nameof(request.Text),
                    "The 24-hour window is closed: WhatsApp only delivers free text within 24 hours of the tenant's last message. Send an approved template instead - when the tenant replies, free text works again.");

            message.Body = text;
            _context.PlatformConversationMessages.Add(message);
            await _context.SaveChangesAsync(cancellationToken);

            result = await _sender.SendTextAsync(conversation.PhoneE164, text, cancellationToken);
        }

        message.WhatsAppMessageId = result.MessageId;
        if (result.Success)
        {
            message.Status = MessageStatus.Sent;
        }
        else
        {
            message.Status = MessageStatus.Failed;
            message.ErrorMessage = Truncate(result.Skipped
                ? "The platform WhatsApp number isn't set up (or is switched off). Set it on the WhatsApp page of the Platform Admin Console."
                : Explain(result.Note), 1000);
        }

        conversation.LastMessageAtUtc = now;
        conversation.LastMessagePreview = Truncate(message.Body, 200);
        await _context.SaveChangesAsync(cancellationToken);

        if (_broadcaster is not null)
            await _broadcaster.ConversationUpdatedAsync(conversation.Id, cancellationToken);

        return ToDto(message);
    }

    public async Task<PlatformConversationInviteDto> GetInviteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var conversation = await _context.PlatformConversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new NotFoundException(nameof(PlatformConversation), id);

        string? number = null;
        if (_settings is not null)
        {
            var stored = await _settings.GetAllAsync(cancellationToken);
            if (stored.TryGetValue(PlatformWhatsAppSettingsService.DisplayPhoneNumberKey, out var raw) && PhoneNumberNormalizer.TryNormalize(raw, out var n))
                number = n;
        }

        var link = number is null ? null : $"https://wa.me/{new string(number.Where(char.IsDigit).ToArray())}?text={Uri.EscapeDataString(InviteText)}";
        var (email, source) = await TenantEmailAsync(conversation.TenantId, cancellationToken);
        return new PlatformConversationInviteDto(number, link, email, source, InviteText);
    }

    public async Task<PlatformConversationMessageDto> SendInviteEmailAsync(
        Guid id, SendPlatformConversationInviteRequest request, Guid? userId, string? userEmail, CancellationToken cancellationToken = default)
    {
        var invite = await GetInviteAsync(id, cancellationToken);
        if (invite.WhatsAppLink is null)
            throw Invalid("Link", "The platform's WhatsApp number isn't saved yet. Open Platform WhatsApp and press Verify (or type the number) first.");
        if (invite.Email is null)
            throw Invalid("Email", "This tenant has no email on file.");
        if (_email is null)
            throw new InvalidOperationException("No email sender is registered.");

        var conversation = await _context.PlatformConversations.FirstAsync(c => c.Id == id, cancellationToken);
        var tenantName = conversation.TenantId is { } tid
            ? await _context.Tenants.IgnoreQueryFilters().Where(t => t.Id == tid).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken)
            : null;

        var note = request.Note?.Trim();
        if (note is { Length: > 1000 })
            throw Invalid(nameof(request.Note), "Keep the note under 1000 characters.");

        const string subject = "The AiSaarthi team would like to chat with you on WhatsApp";
        var body =
            $"Hi {tenantName ?? conversation.ContactName ?? "there"},\n\n" +
            (string.IsNullOrEmpty(note) ? string.Empty : note + "\n\n") +
            $"The AiSaarthi team would like to talk to you on WhatsApp. Please send us a message on {invite.PlatformNumber} - tap this link and press Send:\n\n" +
            $"{invite.WhatsAppLink}\n\n" +
            "Once you message us, we can reply to you directly there.\n\nThanks,\nTeam AiSaarthi";

        var now = _dateTime.UtcNow;
        var sent = await _email.SendAsync(invite.Email, subject, body, cancellationToken);
        var message = new PlatformConversationMessage
        {
            ConversationId = id,
            Direction = MessageDirection.Outbound,
            MessageType = "email",
            Body = Truncate($"Email to {invite.Email}: asked to message {invite.PlatformNumber} on WhatsApp ({invite.WhatsAppLink})" + (string.IsNullOrEmpty(note) ? string.Empty : $"\n\n{note}"), 4096),
            Status = sent.Success ? MessageStatus.Sent : MessageStatus.Failed,
            ErrorMessage = sent.Success ? null : Truncate(sent.Skipped ? "Email isn't set up on this platform (SMTP/SMS Settings)." : sent.Note ?? "The email could not be sent.", 1000),
            SentByUserId = userId,
            SentByEmail = userEmail,
            SentAtUtc = now
        };
        _context.PlatformConversationMessages.Add(message);
        conversation.LastMessageAtUtc = now;
        conversation.LastMessagePreview = Truncate($"Email invite to {invite.Email}", 200);
        await _context.SaveChangesAsync(cancellationToken);

        if (_broadcaster is not null)
            await _broadcaster.ConversationUpdatedAsync(id, cancellationToken);

        return ToDto(message);
    }

    /// <summary>Where to email a tenant: its billing alert email, else the owner's login email, else its support email.</summary>
    private async Task<(string? Email, string? Source)> TenantEmailAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        if (tenantId is not { } tid)
            return (null, null);

        var tenant = await _context.Tenants.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == tid).Select(t => new { t.BillingAlertEmail, t.SupportEmail, t.OwnerUserId }).FirstOrDefaultAsync(cancellationToken);
        if (tenant is null)
            return (null, null);
        if (!string.IsNullOrWhiteSpace(tenant.BillingAlertEmail))
            return (tenant.BillingAlertEmail.Trim(), "Billing alerts");

        if (tenant.OwnerUserId is { } owner)
        {
            var ownerEmail = await _userManager.Users.IgnoreQueryFilters().Where(u => u.Id == owner).Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(ownerEmail))
                return (ownerEmail.Trim(), "Owner");
        }

        return string.IsNullOrWhiteSpace(tenant.SupportEmail) ? (null, null) : (tenant.SupportEmail.Trim(), "Support");
    }

    public async Task RecordWebhookAsync(WhatsAppWebhookParseResult parsed, CancellationToken cancellationToken = default)
    {
        var touched = new HashSet<Guid>();

        foreach (var inbound in parsed.Messages)
        {
            if (!string.IsNullOrEmpty(inbound.WhatsAppMessageId)
                && await _context.PlatformConversationMessages.AnyAsync(m => m.WhatsAppMessageId == inbound.WhatsAppMessageId, cancellationToken))
                continue; // Meta redelivered it.

            // Meta sends the full international number without the "+".
            var from = inbound.FromPhone.Trim();
            if (!PhoneNumberNormalizer.TryNormalize(from.StartsWith('+') ? from : "+" + from, out var phone))
            {
                _logger.LogWarning("Platform WhatsApp number received a message from an unusable number {From}", inbound.FromPhone);
                continue;
            }

            var conversation = await _context.PlatformConversations.FirstOrDefaultAsync(c => c.PhoneE164 == phone, cancellationToken);
            if (conversation is null)
            {
                conversation = new PlatformConversation { PhoneE164 = phone, TenantId = await FindTenantByPhoneAsync(phone, cancellationToken) };
                _context.PlatformConversations.Add(conversation);
            }

            var at = inbound.Timestamp == default ? _dateTime.UtcNow : DateTime.SpecifyKind(inbound.Timestamp, DateTimeKind.Utc);
            var body = string.IsNullOrWhiteSpace(inbound.TextBody) ? $"[{inbound.MessageType}]" : inbound.TextBody;

            _context.PlatformConversationMessages.Add(new PlatformConversationMessage
            {
                ConversationId = conversation.Id,
                Direction = MessageDirection.Inbound,
                Body = Truncate(body, 4096),
                MessageType = Truncate(inbound.MessageType, 30) ?? "text",
                Status = MessageStatus.Delivered,
                WhatsAppMessageId = inbound.WhatsAppMessageId,
                SentAtUtc = at
            });

            if (!string.IsNullOrWhiteSpace(inbound.ContactName))
                conversation.ContactName = Truncate(inbound.ContactName, 200);
            if (conversation.LastInboundAtUtc is null || at > conversation.LastInboundAtUtc)
                conversation.LastInboundAtUtc = at;
            if (conversation.LastMessageAtUtc is null || at >= conversation.LastMessageAtUtc)
            {
                conversation.LastMessageAtUtc = at;
                conversation.LastMessagePreview = Truncate(body, 200);
            }
            conversation.UnreadCount++;

            await _context.SaveChangesAsync(cancellationToken);
            touched.Add(conversation.Id);
        }

        foreach (var status in parsed.Statuses)
        {
            if (string.IsNullOrEmpty(status.WhatsAppMessageId))
                continue;

            var message = await _context.PlatformConversationMessages.FirstOrDefaultAsync(
                m => m.WhatsAppMessageId == status.WhatsAppMessageId && m.Direction == MessageDirection.Outbound, cancellationToken);
            if (message is null)
                continue; // A notice (not a chat message) - nothing to update here.

            var next = status.Status.ToLowerInvariant() switch
            {
                "sent" => MessageStatus.Sent,
                "delivered" => MessageStatus.Delivered,
                "read" => MessageStatus.Read,
                "failed" => MessageStatus.Failed,
                _ => (MessageStatus?)null
            };
            if (next is not { } n)
                continue;

            // Webhooks can arrive out of order: never step a Read message back to Delivered.
            if (n == MessageStatus.Failed)
            {
                message.Status = MessageStatus.Failed;
                message.ErrorMessage = Truncate(Explain(status.FailureReason), 1000);
            }
            else if (message.Status != MessageStatus.Failed && Rank(n) > Rank(message.Status))
            {
                message.Status = n;
            }
            else
            {
                continue;
            }

            await _context.SaveChangesAsync(cancellationToken);
            touched.Add(message.ConversationId);
        }

        if (_broadcaster is not null)
            foreach (var conversationId in touched)
                await _broadcaster.ConversationUpdatedAsync(conversationId, cancellationToken);
    }

    private static int Rank(MessageStatus s) => s switch
    {
        MessageStatus.Queued => 0,
        MessageStatus.Sent => 1,
        MessageStatus.Delivered => 2,
        MessageStatus.Read => 3,
        _ => -1
    };

    /// <summary>Meta's errors, in words an admin can act on. 131047 is the one this page meets most: the 24-hour window had closed.</summary>
    private static string Explain(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return "WhatsApp did not accept the message.";
        if (note.Contains("131047") || note.Contains("Re-engagement", StringComparison.OrdinalIgnoreCase))
            return "The 24-hour window had closed, so WhatsApp did not deliver the free text. Send an approved template instead. (" + note + ")";
        return note;
    }

    private bool IsWindowOpen(PlatformConversation c, DateTime now) => c.LastInboundAtUtc is { } last && now - last < Window;

    private PlatformConversationDto ToDto(PlatformConversation c, IReadOnlyDictionary<Guid, string> names)
    {
        var open = IsWindowOpen(c, _dateTime.UtcNow);
        return new PlatformConversationDto(
            c.Id, c.TenantId, c.TenantId is { } t ? names.GetValueOrDefault(t) : null, c.PhoneE164, c.ContactName,
            c.LastMessageAtUtc, c.LastInboundAtUtc, c.LastMessagePreview, c.UnreadCount, open, open ? c.LastInboundAtUtc!.Value.Add(Window) : null);
    }

    private static PlatformConversationMessageDto ToDto(PlatformConversationMessage m) =>
        new(m.Id, m.Direction, m.Body, m.MessageType, m.TemplateName, m.Status, m.ErrorMessage, m.SentByEmail, m.SentAtUtc);

    private async Task<IReadOnlyDictionary<Guid, string>> TenantNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0)
            return new Dictionary<Guid, string>();
        return await _context.Tenants.IgnoreQueryFilters().AsNoTracking().Where(t => list.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
    }

    /// <summary>Which tenant a number belongs to: one whose billing alert, WhatsApp or support number - or one of whose users' phone - is the same
    /// number once both are normalised.</summary>
    private async Task<Guid?> FindTenantByPhoneAsync(string phoneE164, CancellationToken cancellationToken)
    {
        var digits = new string(phoneE164.Where(char.IsDigit).ToArray());
        var tail = digits.Length > 10 ? digits[^10..] : digits;
        if (tail.Length < 6)
            return null;

        // Every tenant's numbers, compared in memory: people type "098765 43210", which no SQL LIKE on the digits finds. Three short columns
        // per tenant, and only read when a number with no chat yet writes in.
        var tenants = await _context.Tenants.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.BillingAlertPhoneE164 != null || t.WhatsAppNumber != null || t.SupportPhone != null)
            .Select(t => new { t.Id, t.BillingAlertPhoneE164, t.WhatsAppNumber, t.SupportPhone })
            .ToListAsync(cancellationToken);

        foreach (var t in tenants)
            if (PhonesOf(t.BillingAlertPhoneE164, t.WhatsAppNumber, t.SupportPhone, null).Any(p => p.PhoneE164 == phoneE164))
                return t.Id;

        var owners = await _userManager.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.TenantId != null && u.PhoneNumber != null && u.PhoneNumber.Contains(tail))
            .Select(u => new { u.TenantId, u.PhoneNumber })
            .ToListAsync(cancellationToken);

        foreach (var u in owners)
            if (PhoneNumberNormalizer.TryNormalize(u.PhoneNumber, out var p) && p == phoneE164)
                return u.TenantId;

        return null;
    }

    private static IReadOnlyList<PlatformTenantPhoneDto> PhonesOf(string? billing, string? whatsApp, string? support, string? owner)
    {
        var result = new List<PlatformTenantPhoneDto>();
        void Add(string label, string? raw)
        {
            if (PhoneNumberNormalizer.TryNormalize(raw, out var p) && result.All(r => r.PhoneE164 != p))
                result.Add(new PlatformTenantPhoneDto(label, p));
        }

        Add("Billing alerts", billing);
        Add("Owner", owner);
        Add("Business WhatsApp", whatsApp);
        Add("Support", support);
        return result;
    }

    private static string Render(string body, string tenantName, string title, string message)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [PlatformTemplateCatalog.TenantNameToken] = PlatformTemplateCatalog.CleanValue(tenantName, "there"),
            [PlatformTemplateCatalog.TitleToken] = PlatformTemplateCatalog.CleanValue(title, "an update"),
            [PlatformTemplateCatalog.MessageToken] = PlatformTemplateCatalog.CleanValue(message, "Please open your dashboard for details.")
        };
        return TokenPattern.Replace(body, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : "-");
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    private static ValidationException Invalid(string property, string message) =>
        new(new[] { new ValidationFailure(property, message) });
}
