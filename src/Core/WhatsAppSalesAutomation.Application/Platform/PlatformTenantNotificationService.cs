using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>The ways a tenant is told something.</summary>
public static class NotificationChannels
{
    public const string InApp = "InApp";
    public const string Email = "Email";
    public const string WhatsApp = "WhatsApp";

    public static readonly string[] All = { InApp, Email, WhatsApp };
}

/// <summary>Where one channel of a notification stands. <c>Pending</c> is not attempted yet; <c>Skipped</c> means the channel did not apply (no address, or the tenant
/// turned it off), which is not a failure.</summary>
public static class NotificationStatuses
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";

    public static readonly string[] All = { Pending, Sent, Failed, Skipped };
}

/// <summary>One channel of one notification sent to a tenant. A notification with three channels is three rows, so the list can be read and filtered by mode and status.
/// <paramref name="DeliveryNote"/> is why a channel failed or was skipped, for support (null for a channel that went fine). <paramref name="AcknowledgedAtUtc"/> is when the
/// tenant dismissed the in-app copy.</summary>
public record PlatformTenantNotificationDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string Kind,
    string Title,
    string Body,
    string Channel,
    string Status,
    DateTime CreatedAtUtc,
    string? DeliveryNote,
    DateTime? AcknowledgedAtUtc);

public record PlatformTenantNotificationQuery : PagedRequest
{
    /// <summary>InApp, Email or WhatsApp. Left out: every channel.</summary>
    public string? Channel { get; init; }

    /// <summary>Pending, Sent, Failed or Skipped. Left out: every status.</summary>
    public string? Status { get; init; }

    public Guid? TenantId { get; init; }

    /// <summary>One kind of notice, by its name (e.g. NextStepBooked). Left out: every kind. <see cref="IPlatformTenantNotificationService.GetKinds"/> lists them.</summary>
    public string? Kind { get; init; }
}

/// <summary>The Platform Admin Console's list of what tenants were told: every billing, quota, job and booking notice, by channel and how its delivery went. Read-only.</summary>
public interface IPlatformTenantNotificationService
{
    Task<PagedResult<PlatformTenantNotificationDto>> GetPagedAsync(PlatformTenantNotificationQuery query, CancellationToken cancellationToken = default);

    /// <summary>The names of every kind of notice a tenant can be sent, for the page's filter.</summary>
    IReadOnlyList<string> GetKinds();
}

public class PlatformTenantNotificationService : IPlatformTenantNotificationService
{
    private readonly IApplicationDbContext _context;

    public PlatformTenantNotificationService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<PagedResult<PlatformTenantNotificationDto>> GetPagedAsync(PlatformTenantNotificationQuery query, CancellationToken cancellationToken = default)
    {
        string? channel = null;
        if (!string.IsNullOrWhiteSpace(query.Channel))
            channel = NotificationChannels.All.FirstOrDefault(c => string.Equals(c, query.Channel.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid(nameof(query.Channel), "Choose InApp, Email or WhatsApp.");

        string? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
            status = NotificationStatuses.All.FirstOrDefault(s => string.Equals(s, query.Status.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid(nameof(query.Status), "Choose Pending, Sent, Failed or Skipped.");

        TenantNotificationKind? kind = null;
        if (!string.IsNullOrWhiteSpace(query.Kind))
            kind = Enum.TryParse<TenantNotificationKind>(query.Kind.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw Invalid(nameof(query.Kind), "That is not a kind of notification.");

        var source = _context.TenantNotifications.AsNoTracking().IgnoreQueryFilters()
            .Join(_context.Tenants.AsNoTracking(), n => n.TenantId, t => t.Id, (n, t) => new { n, TenantName = t.Name });

        if (query.TenantId is { } tenantId)
            source = source.Where(x => x.n.TenantId == tenantId);

        if (kind is { } wantedKind)
            source = source.Where(x => x.n.Kind == wantedKind);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(x => x.n.Title.Contains(search) || x.n.Body.Contains(search) || x.TenantName.Contains(search));
        }

        // The in-app copy IS the row, so it always exists and always counts as sent; the other two carry their own delivery status.
        var inApp = source.Select(x => new Flat
        {
            Id = x.n.Id, TenantId = x.n.TenantId, TenantName = x.TenantName, Kind = x.n.Kind, Title = x.n.Title, Body = x.n.Body, Channel = NotificationChannels.InApp,
            Delivery = NotificationStatuses.Sent, CreatedAtUtc = x.n.CreatedAt, DeliveryNote = null, AcknowledgedAtUtc = x.n.AcknowledgedAtUtc
        });
        var email = source.Select(x => new Flat
        {
            Id = x.n.Id, TenantId = x.n.TenantId, TenantName = x.TenantName, Kind = x.n.Kind, Title = x.n.Title, Body = x.n.Body, Channel = NotificationChannels.Email,
            Delivery = x.n.EmailStatus == DeliveryStatus.Sent ? NotificationStatuses.Sent : x.n.EmailStatus == DeliveryStatus.Failed ? NotificationStatuses.Failed : x.n.EmailStatus == DeliveryStatus.Skipped ? NotificationStatuses.Skipped : NotificationStatuses.Pending, CreatedAtUtc = x.n.CreatedAt, DeliveryNote = x.n.DeliveryNote, AcknowledgedAtUtc = null
        });
        var whatsApp = source.Select(x => new Flat
        {
            Id = x.n.Id, TenantId = x.n.TenantId, TenantName = x.TenantName, Kind = x.n.Kind, Title = x.n.Title, Body = x.n.Body, Channel = NotificationChannels.WhatsApp,
            Delivery = x.n.WhatsAppStatus == DeliveryStatus.Sent ? NotificationStatuses.Sent : x.n.WhatsAppStatus == DeliveryStatus.Failed ? NotificationStatuses.Failed : x.n.WhatsAppStatus == DeliveryStatus.Skipped ? NotificationStatuses.Skipped : NotificationStatuses.Pending, CreatedAtUtc = x.n.CreatedAt, DeliveryNote = x.n.DeliveryNote, AcknowledgedAtUtc = null
        });

        var rows = channel switch
        {
            NotificationChannels.InApp => inApp,
            NotificationChannels.Email => email,
            NotificationChannels.WhatsApp => whatsApp,
            _ => inApp.Concat(email).Concat(whatsApp)
        };

        if (status is not null)
        {
            var wanted = status;
            rows = rows.Where(r => r.Delivery == wanted);
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows
            .OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.Id).ThenBy(r => r.Channel)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        var items = page.Select(r => new PlatformTenantNotificationDto(
            r.Id, r.TenantId, r.TenantName, r.Kind.ToString(), r.Title, r.Body, r.Channel, r.Delivery, r.CreatedAtUtc,
            // A note explains a channel that did not go; on a channel that did, it would only confuse.
            r.Delivery is NotificationStatuses.Failed or NotificationStatuses.Skipped ? r.DeliveryNote : null, r.AcknowledgedAtUtc)).ToList();

        return new PagedResult<PlatformTenantNotificationDto>(items, total, query.Page, query.PageSize);
    }

    public IReadOnlyList<string> GetKinds() => Enum.GetNames<TenantNotificationKind>();

    /// <summary>One channel of one notification, the shape the three channels are brought to so they can be listed together.</summary>
    private sealed class Flat
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public string TenantName { get; set; } = string.Empty;
        public TenantNotificationKind Kind { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public string Delivery { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public string? DeliveryNote { get; set; }
        public DateTime? AcknowledgedAtUtc { get; set; }
    }

    private static ValidationException Invalid(string property, string message) => new(new[] { new ValidationFailure(property, message) });
}
