using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public record PlatformNotificationDto(
    Guid Id,
    PlatformNotificationKind Kind,
    PlatformNotificationSeverity Severity,
    Guid? TenantId,
    string? TenantName,
    string? JobType,
    string Title,
    string Body,
    DateTime CreatedAt,
    bool Acknowledged);

/// <summary>The platform operators' inbox - the alerts <c>IPlatformNotifier</c> raises. Shared by every
/// PlatformSuperAdmin: acknowledging one clears it for all of them.</summary>
public interface IPlatformNotificationService
{
    /// <summary>Unacknowledged alerts first, then the newest acknowledged ones, capped.</summary>
    Task<IReadOnlyList<PlatformNotificationDto>> GetRecentAsync(CancellationToken cancellationToken = default);

    /// <summary>The whole inbox, newest first, a page at a time - the bell's "More notifications" screen. Unlike
    /// <see cref="GetRecentAsync"/> nothing is dropped: <paramref name="unreadOnly"/> narrows it, and
    /// <see cref="PagedRequest.Search"/> matches the title, body or tenant name.</summary>
    Task<PagedResult<PlatformNotificationDto>> GetHistoryAsync(PagedRequest request, bool unreadOnly, CancellationToken cancellationToken = default);

    Task AcknowledgeAsync(Guid id, CancellationToken cancellationToken = default);

    Task AcknowledgeAllAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public class PlatformNotificationService : IPlatformNotificationService
{
    private const int MaxReturned = 50;

    private readonly IApplicationDbContext _context;
    private readonly IDateTimeProvider _dateTime;

    public PlatformNotificationService(IApplicationDbContext context, IDateTimeProvider dateTime)
    {
        _context = context;
        _dateTime = dateTime;
    }

    public async Task<IReadOnlyList<PlatformNotificationDto>> GetRecentAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _context.PlatformNotifications
            .OrderBy(n => n.AcknowledgedAtUtc != null)
            .ThenByDescending(n => n.CreatedAt)
            .Take(MaxReturned)
            .ToListAsync(cancellationToken);

        return await ToDtosAsync(rows, cancellationToken);
    }

    public async Task<PagedResult<PlatformNotificationDto>> GetHistoryAsync(PagedRequest request, bool unreadOnly, CancellationToken cancellationToken = default)
    {
        var query = _context.PlatformNotifications.AsQueryable();

        if (unreadOnly)
            query = query.Where(n => n.AcknowledgedAtUtc == null);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            // Tenant names live on another table, so the matching tenants are resolved first rather than joined per row.
            var tenantIds = await _context.Tenants.IgnoreQueryFilters()
                .Where(t => t.Name.Contains(term))
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            query = query.Where(n => n.Title.Contains(term) || n.Body.Contains(term) || (n.TenantId != null && tenantIds.Contains(n.TenantId.Value)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<PlatformNotificationDto>(await ToDtosAsync(rows, cancellationToken), total, request.Page, request.PageSize);
    }

    private async Task<IReadOnlyList<PlatformNotificationDto>> ToDtosAsync(IReadOnlyList<PlatformNotification> rows, CancellationToken cancellationToken)
    {
        var tenantIds = rows.Where(r => r.TenantId != null).Select(r => r.TenantId!.Value).Distinct().ToList();
        var names = await _context.Tenants.IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        return rows.Select(n => new PlatformNotificationDto(
            n.Id, n.Kind, n.Severity, n.TenantId,
            n.TenantId is { } id && names.TryGetValue(id, out var name) ? name : null,
            n.JobType, n.Title, n.Body, n.CreatedAt, n.AcknowledgedAtUtc != null)).ToList();
    }

    public async Task AcknowledgeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _context.PlatformNotifications.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (row is null || row.AcknowledgedAtUtc is not null)
            return;

        row.AcknowledgedAtUtc = _dateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AcknowledgeAllAsync(CancellationToken cancellationToken = default)
    {
        var now = _dateTime.UtcNow;
        var open = await _context.PlatformNotifications.Where(n => n.AcknowledgedAtUtc == null).ToListAsync(cancellationToken);
        foreach (var n in open)
            n.AcknowledgedAtUtc = now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _context.PlatformNotifications.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (row is null)
            return;

        _context.PlatformNotifications.Remove(row);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
