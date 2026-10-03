using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Notifications;

public class TenantNotifier : ITenantNotifier
{
    private readonly IApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailSender _email;
    private readonly IPlatformWhatsAppSender _whatsApp;
    private readonly INotificationBroadcaster _broadcaster;
    private readonly BillingAlertOptions _options;
    private readonly ILogger<TenantNotifier> _logger;
    private readonly IPlatformNoticeTemplates? _templates;

    /// <param name="templates">The platform's own WhatsApp templates, one per kind of notice. When absent (a caller that never sends on
    /// WhatsApp) the single configured "billing_alert" template is used instead.</param>
    public TenantNotifier(
        IApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IEmailSender email,
        IPlatformWhatsAppSender whatsApp,
        INotificationBroadcaster broadcaster,
        IOptionsSnapshot<BillingAlertOptions> options,
        ILogger<TenantNotifier> logger,
        IPlatformNoticeTemplates? templates = null)
    {
        _templates = templates;
        _context = context;
        _userManager = userManager;
        _email = email;
        _whatsApp = whatsApp;
        _broadcaster = broadcaster;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> NotifyAsync(TenantNotificationRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var already = await _context.TenantNotifications.IgnoreQueryFilters().AnyAsync(n =>
                n.TenantId == request.TenantId && n.Kind == request.Kind && n.QuotaType == request.QuotaType && n.EpisodeKey == request.EpisodeKey,
                cancellationToken);
            if (already)
                return false;

            var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == request.TenantId, cancellationToken);
            if (tenant is null)
                return false;

            var notification = new TenantNotification
            {
                TenantId = request.TenantId,
                Kind = request.Kind,
                QuotaType = request.QuotaType,
                EpisodeKey = request.EpisodeKey,
                Title = request.Title,
                Body = request.Body
            };
            _context.TenantNotifications.Add(notification);

            // Saved before any delivery is attempted: the row is the in-app alert and the dedupe marker, so a
            // slow or failing email can never cause the same alert to be raised twice.
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Lost a race with another pass that raised the same alert first.
                _context.ResetChangeTracker();
                return false;
            }

            var notes = new List<string>();

            var emailTo = tenant.BillingAlertEmail;
            if (string.IsNullOrWhiteSpace(emailTo) && tenant.OwnerUserId is { } ownerId)
                emailTo = (await _userManager.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == ownerId, cancellationToken))?.Email;

            if (string.IsNullOrWhiteSpace(emailTo))
            {
                notification.EmailStatus = DeliveryStatus.Skipped;
                notes.Add("email: no address on file");
            }
            else
            {
                var sent = await _email.SendAsync(emailTo, request.Title, request.Body, cancellationToken);
                notification.EmailStatus = sent.Skipped ? DeliveryStatus.Skipped : sent.Success ? DeliveryStatus.Sent : DeliveryStatus.Failed;
                if (sent.Note is not null) notes.Add($"email: {sent.Note}");
            }

            if (!request.AlsoWhatsApp || !tenant.BillingAlertWhatsAppEnabled || string.IsNullOrWhiteSpace(tenant.BillingAlertPhoneE164))
            {
                notification.WhatsAppStatus = DeliveryStatus.Skipped;
                notes.Add(!request.AlsoWhatsApp ? "whatsapp: not used for this notice" : "whatsapp: opted out or no number on file");
            }
            else
            {
                DeliveryResult sent;
                if (_templates is null)
                {
                    sent = await _whatsApp.SendTemplateAsync(
                        tenant.BillingAlertPhoneE164, _options.WhatsAppTemplateName, _options.WhatsAppTemplateLanguage,
                        new[] { tenant.Name, request.Body }, cancellationToken);
                }
                else
                {
                    // The platform admin owns one template per kind of notice; one that is missing, switched off or not yet approved by Meta
                    // is skipped with the reason on the notification - never sent as something else.
                    var resolution = await _templates.ResolveAsync(request.Kind, tenant.Name, request.Title, request.Body, cancellationToken);
                    sent = resolution.Message is { } message
                        ? await _whatsApp.SendTemplateAsync(
                            tenant.BillingAlertPhoneE164, message.TemplateName, message.Language, message.Parameters, cancellationToken, message.MediaUrl)
                        : new DeliveryResult(false, resolution.SkipNote, Skipped: true);
                }

                notification.WhatsAppStatus = sent.Skipped ? DeliveryStatus.Skipped : sent.Success ? DeliveryStatus.Sent : DeliveryStatus.Failed;
                if (sent.Note is not null) notes.Add(sent.Note.StartsWith("whatsapp:") ? sent.Note : $"whatsapp: {sent.Note}");
            }

            notification.DeliveryNote = notes.Count == 0 ? null : string.Join("; ", notes);
            await _context.SaveChangesAsync(cancellationToken);

            await _broadcaster.NotifyTenantAsync(request.TenantId, new
            {
                id = notification.Id,
                kind = notification.Kind,
                quotaType = notification.QuotaType,
                title = notification.Title,
                body = notification.Body,
                emailStatus = notification.EmailStatus,
                whatsAppStatus = notification.WhatsAppStatus,
                createdAt = notification.CreatedAt,
                acknowledged = notification.AcknowledgedAtUtc is not null
            }, cancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not raise {Kind} notification for tenant {TenantId}", request.Kind, request.TenantId);
            return false;
        }
    }
}
