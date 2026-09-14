using WhatsAppSalesAutomation.Application.Common.Models;

namespace WhatsAppSalesAutomation.Application.Platform;

public record PlatformAuditLogEntryDto(
    Guid Id,
    Guid ActorUserId,
    string ActorEmail,
    string Action,
    Guid? TargetTenantId,
    string? TargetTenantName,
    Guid? TargetUserId,
    string? Details,
    DateTime CreatedAt);

/// <summary>Filters for the Audit Log screen, on top of PagedRequest's Page/PageSize (Search matches
/// ActorEmail/Action) - <see cref="TargetTenantId"/> narrows to one tenant's history.</summary>
public record PlatformAuditLogQuery : PagedRequest
{
    public Guid? TargetTenantId { get; init; }
}
