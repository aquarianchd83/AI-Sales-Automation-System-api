using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>
/// The accountability trail (request #10 of the Platform Admin Console spec) for everything a
/// PlatformSuperAdmin does that reaches across or affects a tenant. Every other Platform* service
/// that performs a write calls <see cref="LogAsync"/> as part of that same action - this service never
/// initiates anything itself, it only records.
/// </summary>
public interface IPlatformAuditService
{
    /// <summary>Records one entry. Deliberately takes the acting user's id/email as plain parameters
    /// rather than reading <c>ICurrentUserService</c> itself - every call site already has the
    /// authenticated PlatformSuperAdmin's identity from its own controller, and a fixed signature keeps
    /// this service trivially unit-testable without an HTTP context.</summary>
    Task LogAsync(
        Guid actorUserId,
        string actorEmail,
        string action,
        Guid? targetTenantId = null,
        Guid? targetUserId = null,
        string? details = null,
        CancellationToken cancellationToken = default);

    Task<PagedResult<PlatformAuditLogEntryDto>> GetPagedAsync(PlatformAuditLogQuery query, CancellationToken cancellationToken = default);
}
