using FluentValidation;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Entities.Audit;

namespace WhatsAppSalesAutomation.Application.Audit;

/// <summary>Filters for the audit log. <see cref="PagedRequest.Search"/> matches the entity name.</summary>
public record AuditLogQuery : PagedRequest
{
    /// <summary>"Lead", "Campaign", "Conversation", "Handoff", "Customer", "KnowledgeArticle".</summary>
    public string? EntityName { get; init; }

    /// <summary>With <see cref="EntityName"/>, the full history of one record.</summary>
    public Guid? EntityId { get; init; }

    /// <summary>Create, Update, Delete or StatusChange.</summary>
    public string? Action { get; init; }

    public Guid? PerformedBy { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }
}

/// <param name="PerformedByName">Null when <c>PerformedBy</c> is null (the system acted) OR when that
/// user no longer exists. The two are distinguishable by <c>PerformedBy</c> itself.</param>
public record AuditLogEntryDto(
    Guid Id,
    string EntityName,
    Guid EntityId,
    string Action,
    string ChangesJson,
    Guid? PerformedBy,
    string? PerformedByName,
    Guid? ImpersonatedBy,
    DateTime PerformedAt,
    string? IpAddress);

public interface IAuditLogService
{
    Task<PagedResult<AuditLogEntryDto>> GetPagedAsync(AuditLogQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the tenant's audit trail. Read-only by construction: there is no write path here, and the
/// table itself refuses modification (see AuditTrailSaveChangesInterceptor). Tenant isolation is the
/// ordinary query filter - an <see cref="AuditLog"/> is tenant-owned like any other row.
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    private readonly IApplicationDbContext _context;

    public AuditLogService(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<PagedResult<AuditLogEntryDto>> GetPagedAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        AuditAction? action = null;
        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            if (!Enum.TryParse<AuditAction>(query.Action, ignoreCase: true, out var parsed))
                throw Invalid("action", $"Action must be one of: {string.Join(", ", Enum.GetNames<AuditAction>())}.");

            action = parsed;
        }

        if (query.From is { } from && query.To is { } to && from > to)
            throw Invalid("from", "'from' must not be later than 'to'.");

        var logs = _context.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.EntityName))
            logs = logs.Where(a => a.EntityName == query.EntityName);
        else if (!string.IsNullOrWhiteSpace(query.Search))
            logs = logs.Where(a => a.EntityName.Contains(query.Search.Trim()));

        if (query.EntityId is { } entityId) logs = logs.Where(a => a.EntityId == entityId);
        if (action is { } a2) logs = logs.Where(a => a.Action == a2);
        if (query.PerformedBy is { } actor) logs = logs.Where(a => a.PerformedBy == actor);
        if (query.From is { } f) logs = logs.Where(a => a.PerformedAt >= f);
        if (query.To is { } t) logs = logs.Where(a => a.PerformedAt <= t);

        var total = await logs.CountAsync(cancellationToken);

        var rows = await logs
            .OrderByDescending(a => a.PerformedAt).ThenByDescending(a => a.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        // Names in one extra query for the page, not a join per row.
        var actorIds = rows.Where(r => r.PerformedBy is not null).Select(r => r.PerformedBy!.Value).Distinct().ToList();
        var names = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Users.AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var items = rows.Select(r => new AuditLogEntryDto(
            r.Id, r.EntityName, r.EntityId, r.Action.ToString(), r.ChangesJson, r.PerformedBy,
            r.PerformedBy is { } id && names.TryGetValue(id, out var name) ? name : null,
            r.ImpersonatedBy, r.PerformedAt, r.IpAddress)).ToList();

        return new PagedResult<AuditLogEntryDto>(items, total, query.Page, query.PageSize);
    }

    private static ValidationException Invalid(string property, string message) =>
        new(new[] { new FluentValidation.Results.ValidationFailure(property, message) });
}
