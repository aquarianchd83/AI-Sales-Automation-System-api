using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Compliance;

/// <summary>Acts on Meta telling us a tenant's WhatsApp number changed quality, and lets a person reopen sending.</summary>
public interface ITenantQualityService
{
    /// <summary>
    /// Applies one <c>phone_number_quality_update</c>. <paramref name="metaEvent"/> is Meta's event name
    /// (FLAGGED, UNFLAGGED, DOWNGRADE, UPGRADE, ONBOARDING); <paramref name="explicitRating"/> is a GREEN/YELLOW/RED
    /// value when the payload carries one, and wins over the event. Safe to call twice with the same input.
    /// </summary>
    Task ApplyPhoneQualityEventAsync(Guid tenantId, string metaEvent, string? explicitRating, CancellationToken cancellationToken = default);

    /// <summary>Reopens sending for a tenant whose quality-triggered pause a person has reviewed.</summary>
    Task ResumeSendingAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default);
}

/// <summary>
/// The automatic actions for a quality change:
/// <list type="bullet">
/// <item>Yellow: warn the tenant. The daily cap halves (see <see cref="SendLimits.EffectiveDailyCap"/>).</item>
/// <item>Red: pause sending and flag the account for manual review. A recovered rating does not reopen sending.</item>
/// <item>A repeat violation - the number dropped again, within <see cref="RepeatViolationWindowDays"/> of a previous
/// warning, after having recovered - suspends the tenant.</item>
/// <item>Green again: restarts the clock that earns higher caps.</item>
/// </list>
/// Every action is written to the platform audit log, which is how our team is alerted; the tenant is told directly.
/// </summary>
public sealed class TenantQualityService : ITenantQualityService
{
    /// <summary>ASSUMPTION - the plan says "repeat violations after a warning" without a window. 30 days, to tune.</summary>
    public const int RepeatViolationWindowDays = 30;

    private static readonly Guid SystemActor = Guid.Empty;
    private const string SystemEmail = "system@whatsapp-quality";

    private readonly IApplicationDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly ITenantNotifier _notifier;
    private readonly IPlatformAuditService _audit;
    private readonly IDateTimeProvider _dateTime;
    private readonly ILogger<TenantQualityService> _logger;

    public TenantQualityService(
        IApplicationDbContext context,
        ITenantContext tenantContext,
        ITenantNotifier notifier,
        IPlatformAuditService audit,
        IDateTimeProvider dateTime,
        ILogger<TenantQualityService> logger)
    {
        _context = context;
        _tenantContext = tenantContext;
        _notifier = notifier;
        _audit = audit;
        _dateTime = dateTime;
        _logger = logger;
    }

    public async Task ApplyPhoneQualityEventAsync(Guid tenantId, string metaEvent, string? explicitRating, CancellationToken cancellationToken = default)
    {
        // The webhook that calls this is anonymous: say which tenant the notification below belongs to.
        _tenantContext.SetTenant(tenantId);

        var rating = ResolveRating(metaEvent, explicitRating);
        if (rating is null)
        {
            _logger.LogInformation("Ignoring phone number quality event {Event} for tenant {TenantId}: it does not change the rating", metaEvent, tenantId);
            return;
        }

        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

        // Meta redelivers webhooks; the same rating twice is not a second incident.
        if (tenant.QualityRating == rating.Value)
            return;

        var now = _dateTime.UtcNow;
        var previous = tenant.QualityRating;
        tenant.QualityRating = rating.Value;
        tenant.QualityRatingSince = now;

        if (rating.Value == WhatsAppQualityRating.Green)
        {
            await _context.SaveChangesAsync(cancellationToken);
            await AuditAsync(PlatformAuditActions.TenantQualityRecovered, tenantId, $"WhatsApp quality is back to Green (was {previous}).", cancellationToken);
            return;
        }

        // A drop from a healthy number after an earlier warning is a repeat; Yellow turning Red is the same incident
        // getting worse, not a repeat.
        var repeat = previous == WhatsAppQualityRating.Green
            && tenant.QualityWarningAt is { } lastWarning
            && now - lastWarning <= TimeSpan.FromDays(RepeatViolationWindowDays);

        tenant.QualityWarningAt = now;

        if (rating.Value == WhatsAppQualityRating.Red)
        {
            tenant.SendingPausedAt = now;
            tenant.SendingPauseReason = "WhatsApp rated the number's quality Red, so sending is paused while the account is reviewed.";
        }

        if (repeat)
            tenant.Status = TenantStatus.Suspended;

        await _context.SaveChangesAsync(cancellationToken);

        var details = $"WhatsApp quality went {previous} -> {rating.Value}.";
        await AuditAsync(
            rating.Value == WhatsAppQualityRating.Red ? PlatformAuditActions.TenantSendingPaused : PlatformAuditActions.TenantQualityWarning,
            tenantId, details, cancellationToken);

        if (repeat)
            await AuditAsync(PlatformAuditActions.TenantSuspended, tenantId, "Suspended automatically: WhatsApp quality dropped again after an earlier warning.", cancellationToken);

        await NotifyTenantAsync(tenantId, rating.Value, repeat, now, cancellationToken);
    }

    public async Task ResumeSendingAsync(Guid tenantId, Guid actorUserId, string actorEmail, CancellationToken cancellationToken = default)
    {
        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException(nameof(Tenant), tenantId);

        if (tenant.SendingPausedAt is null)
            return;

        tenant.SendingPausedAt = null;
        tenant.SendingPauseReason = null;
        await _context.SaveChangesAsync(cancellationToken);
        await _audit.LogAsync(actorUserId, actorEmail, PlatformAuditActions.TenantSendingResumed, tenantId, cancellationToken: cancellationToken);
    }

    /// <summary>An explicit GREEN/YELLOW/RED wins; otherwise the event name decides. Only FLAGGED and DOWNGRADE lower the
    /// rating and only UNFLAGGED restores it; UPGRADE and ONBOARDING change the messaging tier or announce a new number,
    /// not the quality, so they are ignored.</summary>
    internal static WhatsAppQualityRating? ResolveRating(string metaEvent, string? explicitRating)
    {
        switch (explicitRating?.Trim().ToUpperInvariant())
        {
            case "GREEN": return WhatsAppQualityRating.Green;
            case "YELLOW": return WhatsAppQualityRating.Yellow;
            case "RED": return WhatsAppQualityRating.Red;
        }

        return metaEvent?.Trim().ToUpperInvariant() switch
        {
            "FLAGGED" or "DOWNGRADE" => WhatsAppQualityRating.Yellow,
            "UNFLAGGED" => WhatsAppQualityRating.Green,
            _ => null
        };
    }

    private Task AuditAsync(string action, Guid tenantId, string details, CancellationToken cancellationToken)
    {
        _logger.LogWarning("{Action} for tenant {TenantId}: {Details}", action, tenantId, details);
        return _audit.LogAsync(SystemActor, SystemEmail, action, tenantId, details: details, cancellationToken: cancellationToken);
    }

    private Task NotifyTenantAsync(Guid tenantId, WhatsAppQualityRating rating, bool suspended, DateTime now, CancellationToken cancellationToken)
    {
        var red = rating == WhatsAppQualityRating.Red;
        var title = red ? "WhatsApp sending is paused" : "Your WhatsApp quality rating dropped";
        var body = red
            ? "WhatsApp rated your number's quality Red, so we have paused sending while we review your account. Messages will not go out until the review is done."
            : "WhatsApp lowered your number's quality rating. Until it recovers your daily sending limit is halved. Check that every contact has agreed to hear from you and that your messages are relevant.";
        if (suspended)
            body += " Because this has happened before, your account has been suspended.";

        return _notifier.NotifyAsync(
            new TenantNotificationRequest(
                tenantId,
                red ? TenantNotificationKind.SendingPaused : TenantNotificationKind.QualityWarning,
                null,
                $"quality-{rating}-{now:yyyyMMddHHmm}",
                title,
                body,
                AlsoWhatsApp: false),
            cancellationToken);
    }
}
