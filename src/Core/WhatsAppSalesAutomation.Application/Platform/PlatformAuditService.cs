using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Application.Platform;

public class PlatformAuditService : IPlatformAuditService
{
    private readonly IApplicationDbContext _context;

    public PlatformAuditService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task LogAsync(
        Guid actorUserId,
        string actorEmail,
        string action,
        Guid? targetTenantId = null,
        Guid? targetUserId = null,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        _context.PlatformAuditLogEntries.Add(new PlatformAuditLogEntry
        {
            ActorUserId = actorUserId,
            ActorEmail = actorEmail,
            Action = action,
            TargetTenantId = targetTenantId,
            TargetUserId = targetUserId,
            Details = details
        });

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<PlatformAuditLogEntryDto>> GetPagedAsync(PlatformAuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var entries = _context.PlatformAuditLogEntries.AsQueryable();

        if (query.TargetTenantId is { } tenantId)
            entries = entries.Where(e => e.TargetTenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            entries = entries.Where(e => e.ActorEmail.Contains(search) || e.Action.Contains(search));
        }

        var totalCount = await entries.CountAsync(cancellationToken);

        // Left join, not Include - PlatformAuditLogEntry has no navigation property to Tenant (it
        // isn't ITenantOwned, so wiring one up would be solely for this display convenience).
        var items = await entries
            .OrderByDescending(e => e.CreatedAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .GroupJoin(_context.Tenants.IgnoreQueryFilters(), e => e.TargetTenantId, t => (Guid?)t.Id, (e, tenants) => new { e, tenants })
            .SelectMany(x => x.tenants.DefaultIfEmpty(), (x, t) => new PlatformAuditLogEntryDto(
                x.e.Id, x.e.ActorUserId, x.e.ActorEmail, x.e.Action, x.e.TargetTenantId, t == null ? null : t.Name, x.e.TargetUserId, x.e.Details, x.e.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<PlatformAuditLogEntryDto>(items, totalCount, query.Page, query.PageSize);
    }
}
