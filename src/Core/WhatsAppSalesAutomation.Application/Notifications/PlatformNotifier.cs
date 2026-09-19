using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Notifications;

public class PlatformNotifier : IPlatformNotifier
{
    private readonly IApplicationDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IEmailSender _email;
    private readonly ILogger<PlatformNotifier> _logger;

    public PlatformNotifier(
        IApplicationDbContext context,
        UserManager<ApplicationUser> userManager,
        IEmailSender email,
        ILogger<PlatformNotifier> logger)
    {
        _context = context;
        _userManager = userManager;
        _email = email;
        _logger = logger;
    }

    public async Task<bool> NotifyAsync(PlatformNotificationRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var already = await _context.PlatformNotifications.AnyAsync(n =>
                n.Kind == request.Kind && n.TenantId == request.TenantId && n.JobType == request.JobType && n.EpisodeKey == request.EpisodeKey,
                cancellationToken);
            if (already)
                return false;

            var notification = new PlatformNotification
            {
                Kind = request.Kind,
                Severity = request.Severity,
                TenantId = request.TenantId,
                JobType = request.JobType,
                EpisodeKey = request.EpisodeKey,
                Title = request.Title,
                Body = request.Body
            };
            _context.PlatformNotifications.Add(notification);

            // Saved before any email is attempted: the row is the in-app alert and the dedupe marker, so a
            // slow or failing mail server can never cause the same alert to be raised twice.
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                _context.ResetChangeTracker();
                return false;
            }

            // Platform operators are exactly the users with no tenant.
            var recipients = await _userManager.Users.IgnoreQueryFilters()
                .Where(u => u.TenantId == null && u.IsActive && u.Email != null)
                .Select(u => u.Email!)
                .ToListAsync(cancellationToken);

            if (recipients.Count == 0)
            {
                notification.EmailStatus = DeliveryStatus.Skipped;
                notification.DeliveryNote = "email: no platform operator has an address on file";
            }
            else
            {
                int sent = 0, failed = 0;
                var notes = new List<string>();
                foreach (var to in recipients)
                {
                    var result = await _email.SendAsync(to, request.Title, request.Body, cancellationToken);
                    if (!result.Skipped)
                    {
                        if (result.Success) sent++;
                        else failed++;
                    }
                    if (result.Note is not null && !notes.Contains(result.Note)) notes.Add(result.Note);
                }

                notification.EmailStatus = sent > 0 ? DeliveryStatus.Sent : failed > 0 ? DeliveryStatus.Failed : DeliveryStatus.Skipped;
                notification.DeliveryNote = notes.Count == 0 ? null : "email: " + string.Join("; ", notes);
            }

            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not raise {Kind} platform notification", request.Kind);
            return false;
        }
    }
}
