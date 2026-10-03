using System.Text.Json;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Domain.Entities.Audit;

namespace WhatsAppSalesAutomation.Application.Audit;

/// <summary>Records something in the tenant's audit trail that no single entity change describes - a user's set of roles, say, which
/// lives in a join table rather than on the user. Everything else is captured automatically by the save interceptor.</summary>
public interface IAuditTrailWriter
{
    /// <summary>Adds the entry and saves it. Append-only like every other row of the trail.</summary>
    Task RecordAsync(
        Guid tenantId, string entityName, Guid entityId, AuditAction action,
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default);
}

public sealed class AuditTrailWriter : IAuditTrailWriter
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _clock;

    public AuditTrailWriter(IApplicationDbContext context, ICurrentUserService currentUser, IDateTimeProvider clock)
    {
        _context = context;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task RecordAsync(
        Guid tenantId, string entityName, Guid entityId, AuditAction action,
        IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken = default)
    {
        _context.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            EntityName = entityName,
            EntityId = entityId,
            Action = action,
            ChangesJson = JsonSerializer.Serialize(changes),
            PerformedBy = _currentUser.UserId,
            ImpersonatedBy = _currentUser.ImpersonatorUserId,
            PerformedAt = _clock.UtcNow,
            CreatedAt = _clock.UtcNow,
            IpAddress = _currentUser.IpAddress
        });

        await _context.SaveChangesAsync(cancellationToken);
    }
}
