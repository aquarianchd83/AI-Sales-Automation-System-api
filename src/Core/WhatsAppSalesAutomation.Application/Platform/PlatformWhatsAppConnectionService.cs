using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformWhatsAppConnectionService : IPlatformWhatsAppConnectionService
{
    private readonly IApplicationDbContext _context;
    private readonly ITenantWhatsAppConfigProvider _configProvider;
    private readonly IDateTimeProvider _dateTime;

    public PlatformWhatsAppConnectionService(IApplicationDbContext context, ITenantWhatsAppConfigProvider configProvider, IDateTimeProvider dateTime)
    {
        _context = context;
        _configProvider = configProvider;
        _dateTime = dateTime;
    }

    public async Task<IReadOnlyList<PlatformWhatsAppConnectionDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var connections = await _configProvider.GetAllConnectionSummariesAsync(cancellationToken);
        if (connections.Count == 0)
            return Array.Empty<PlatformWhatsAppConnectionDto>();

        var tenantIds = connections.Select(c => c.TenantId).ToList();

        var tenantNames = await _context.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var since = _dateTime.UtcNow.AddHours(-24);

        // WebhookEvent has no TenantId-to-phone-number join column of its own (it's tenant-scoped via
        // the ordinary TenantId column, stamped when the event is routed to a tenant - see
        // InboundWebhookProcessor), so the same tenantIds filter used above applies directly here.
        var webhookStats = await _context.WebhookEvents.IgnoreQueryFilters()
            .Where(w => tenantIds.Contains(w.TenantId) && w.ReceivedAt >= since)
            .GroupBy(w => w.TenantId)
            .Select(g => new
            {
                TenantId = g.Key,
                Total = g.Count(),
                Failed = g.Count(w => w.ProcessingStatus == WebhookProcessingStatus.Failed),
                LastReceivedAt = g.Max(w => w.ReceivedAt)
            })
            .ToDictionaryAsync(g => g.TenantId, cancellationToken);

        return connections
            .OrderByDescending(c => c.UpdatedAtUtc)
            .Select(c =>
            {
                var stats = webhookStats.GetValueOrDefault(c.TenantId);
                return new PlatformWhatsAppConnectionDto(
                    c.TenantId, tenantNames.GetValueOrDefault(c.TenantId, "(unknown tenant)"),
                    c.PhoneNumberId, c.WhatsAppBusinessAccountId, c.IsConnected, c.UpdatedAtUtc,
                    stats?.Total ?? 0, stats?.Failed ?? 0, stats?.LastReceivedAt);
            })
            .ToList();
    }
}
