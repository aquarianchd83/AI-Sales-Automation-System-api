using System.Text.RegularExpressions;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Billing;

public record BillingAlertSettingsDto(string? Email, string? PhoneE164, bool WhatsAppEnabled);

public record UpdateBillingAlertSettingsRequest(string? Email, string? PhoneE164, bool WhatsAppEnabled);

public record TenantNotificationDto(
    Guid Id,
    TenantNotificationKind Kind,
    QuotaType? QuotaType,
    string Title,
    string Body,
    DeliveryStatus EmailStatus,
    DeliveryStatus WhatsAppStatus,
    DateTime CreatedAt,
    bool Acknowledged);

/// <summary>The tenant's side of billing notifications: where alerts are sent, and the in-app list.</summary>
public interface ITenantBillingNoticeService
{
    Task<BillingAlertSettingsDto> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<BillingAlertSettingsDto> UpdateSettingsAsync(Guid tenantId, UpdateBillingAlertSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Most recent first, capped - this is a bell, not an archive.</summary>
    Task<IReadOnlyList<TenantNotificationDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task AcknowledgeAsync(Guid tenantId, Guid notificationId, CancellationToken cancellationToken = default);

    Task AcknowledgeAllAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid tenantId, Guid notificationId, CancellationToken cancellationToken = default);

    /// <summary>Sends one real alert through the same in-app/email/WhatsApp path a genuine one would use -
    /// so a tenant Admin can see exactly how and where an alert shows up without waiting for the real
    /// trigger (a quota actually running out, a plan actually about to renew). Every call is its own
    /// fresh episode, so it is never deduped against a real alert or a previous test; the title is
    /// prefixed "[Test]" so it is never mistaken for a genuine one in the notification list.
    /// <paramref name="quotaType"/> is required for the three quota-threshold kinds and ignored
    /// otherwise.</summary>
    Task<TenantNotificationDto> SendTestAsync(Guid tenantId, TenantNotificationKind kind, QuotaType? quotaType, CancellationToken cancellationToken = default);
}

public partial class TenantBillingNoticeService : ITenantBillingNoticeService
{
    private const int MaxListed = 50;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITenantNotifier _notifier;

    public TenantBillingNoticeService(IApplicationDbContext context, IDateTimeProvider dateTime, ITenantNotifier notifier)
    {
        _context = context;
        _dateTime = dateTime;
        _notifier = notifier;
    }

    public async Task<BillingAlertSettingsDto> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Domain.Entities.Tenancy.Tenant), tenantId);
        return new BillingAlertSettingsDto(tenant.BillingAlertEmail, tenant.BillingAlertPhoneE164, tenant.BillingAlertWhatsAppEnabled);
    }

    public async Task<BillingAlertSettingsDto> UpdateSettingsAsync(Guid tenantId, UpdateBillingAlertSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        var phone = string.IsNullOrWhiteSpace(request.PhoneE164) ? null : request.PhoneE164.Trim();

        var failures = new List<ValidationFailure>();
        if (email is not null && (email.Length > 256 || !EmailPattern().IsMatch(email)))
            failures.Add(new ValidationFailure(nameof(request.Email), "Enter a valid email address."));
        if (phone is not null && !E164Pattern().IsMatch(phone))
            failures.Add(new ValidationFailure(nameof(request.PhoneE164), "Enter the number with its country code, like +919876543210."));
        if (request.WhatsAppEnabled && phone is null)
            failures.Add(new ValidationFailure(nameof(request.PhoneE164), "Add a WhatsApp number to receive alerts there, or turn WhatsApp alerts off."));
        if (failures.Count > 0)
            throw new FluentValidation.ValidationException(failures);

        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Domain.Entities.Tenancy.Tenant), tenantId);

        tenant.BillingAlertEmail = email;
        tenant.BillingAlertPhoneE164 = phone;
        tenant.BillingAlertWhatsAppEnabled = request.WhatsAppEnabled;
        await _context.SaveChangesAsync(cancellationToken);

        return new BillingAlertSettingsDto(tenant.BillingAlertEmail, tenant.BillingAlertPhoneE164, tenant.BillingAlertWhatsAppEnabled);
    }

    public async Task<IReadOnlyList<TenantNotificationDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var rows = await _context.TenantNotifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(MaxListed)
            .ToListAsync(cancellationToken);

        return rows.Select(n => new TenantNotificationDto(
            n.Id, n.Kind, n.QuotaType, n.Title, n.Body, n.EmailStatus, n.WhatsAppStatus, n.CreatedAt, n.AcknowledgedAtUtc is not null)).ToList();
    }

    public async Task AcknowledgeAsync(Guid tenantId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var notification = await _context.TenantNotifications.IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Domain.Entities.Billing.TenantNotification), notificationId);

        if (notification.AcknowledgedAtUtc is not null)
            return;

        notification.AcknowledgedAtUtc = _dateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AcknowledgeAllAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var open = await _context.TenantNotifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId && n.AcknowledgedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var n in open)
            n.AcknowledgedAtUtc = now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid tenantId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var notification = await _context.TenantNotifications.IgnoreQueryFilters()
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Domain.Entities.Billing.TenantNotification), notificationId);

        _context.TenantNotifications.Remove(notification);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TenantNotificationDto> SendTestAsync(Guid tenantId, TenantNotificationKind kind, QuotaType? quotaType, CancellationToken cancellationToken = default)
    {
        var isQuotaKind = kind is TenantNotificationKind.QuotaLow20 or TenantNotificationKind.QuotaLow5 or TenantNotificationKind.QuotaExhausted;
        if (isQuotaKind && quotaType is null)
            throw new FluentValidation.ValidationException(new[]
            {
                new ValidationFailure(nameof(quotaType), "Pick which quota this test alert is about.")
            });

        // Never collides with a real alert, or an earlier test: every send is its own episode.
        var episode = $"test:{Guid.NewGuid():N}";
        var (title, body) = Describe(kind, isQuotaKind ? quotaType : null);

        await _notifier.NotifyAsync(
            new TenantNotificationRequest(tenantId, kind, isQuotaKind ? quotaType : null, episode, title, body, AlsoWhatsApp: true),
            cancellationToken);

        var sent = await _context.TenantNotifications.IgnoreQueryFilters()
            .Where(n => n.TenantId == tenantId && n.Kind == kind && n.EpisodeKey == episode)
            .OrderByDescending(n => n.CreatedAt)
            .FirstAsync(cancellationToken);

        return new TenantNotificationDto(
            sent.Id, sent.Kind, sent.QuotaType, sent.Title, sent.Body, sent.EmailStatus, sent.WhatsAppStatus, sent.CreatedAt, sent.AcknowledgedAtUtc is not null);
    }

    private static (string Title, string Body) Describe(TenantNotificationKind kind, QuotaType? quotaType)
    {
        const string note = " (This is a test alert you triggered — no real quota, plan or refund event happened.)";
        var quotaLabel = quotaType switch
        {
            QuotaType.WhatsAppMessages => "WhatsApp messages",
            QuotaType.AiConversations => "AI conversations",
            _ => "lead candidates",
        };

        return kind switch
        {
            TenantNotificationKind.QuotaLow20 => (
                $"[Test] Your {quotaLabel} are running low",
                $"180 of 1,000 {quotaLabel} are left (under 20%). Buy credits to keep going without a break.{note}"),
            TenantNotificationKind.QuotaLow5 => (
                $"[Test] Your {quotaLabel} are almost gone",
                $"40 of 1,000 {quotaLabel} are left (under 5%). Buy credits now to avoid an interruption.{note}"),
            TenantNotificationKind.QuotaExhausted => (
                $"[Test] You've used all your {quotaLabel}",
                $"Your {quotaLabel} are used up, so anything that needs them is paused. Buy credits or wait for your plan to renew to carry on.{note}"),
            TenantNotificationKind.CreditsExpiring14 => (
                "[Test] Some of your credits expire soon",
                $"500 of your purchased credits expire in the next 14 days, on {DateTime.UtcNow.AddDays(14):d MMM yyyy}. Use them before then.{note}"),
            TenantNotificationKind.CreditsExpiring3 => (
                "[Test] Some of your credits expire soon",
                $"500 of your purchased credits expire in the next 3 days, on {DateTime.UtcNow.AddDays(3):d MMM yyyy}. Use them before then.{note}"),
            TenantNotificationKind.RefundApproved => (
                "[Test] Your refund request was approved",
                $"A refund of ₹1,000 has been approved and will be credited back to your original payment method.{note}"),
            TenantNotificationKind.RefundRejected => (
                "[Test] Your refund request was declined",
                $"Your refund request wasn't approved. Contact support if you think this is wrong.{note}"),
            TenantNotificationKind.RefundExpired => (
                "[Test] Your refund request expired",
                $"Your refund request wasn't answered in time and has expired. You can ask again if you still want a refund.{note}"),
            TenantNotificationKind.PlanExpiring7 => (
                "[Test] Your plan renews in 7 days",
                $"Your plan renews on {DateTime.UtcNow.AddDays(7):d MMM yyyy}. The usual amount will be charged automatically and your quota renewed.{note}"),
            _ => (
                "[Test] Your plan renews tomorrow",
                $"Your plan renews on {DateTime.UtcNow.AddDays(1):d MMM yyyy}. The usual amount will be charged automatically and your quota renewed.{note}"),
        };
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    // E.164: a plus, then 8 to 15 digits, first digit non-zero.
    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex E164Pattern();
}
