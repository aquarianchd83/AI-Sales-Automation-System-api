using System.Text.RegularExpressions;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
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
}

public partial class TenantBillingNoticeService : ITenantBillingNoticeService
{
    private const int MaxListed = 50;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;

    public TenantBillingNoticeService(IApplicationDbContext context, IDateTimeProvider dateTime)
    {
        _context = context;
        _dateTime = dateTime;
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

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    // E.164: a plus, then 8 to 15 digits, first digit non-zero.
    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex E164Pattern();
}
