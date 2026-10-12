using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.ContentStudio;

public record StudioActivityDto(
    Guid Id,
    StudioActivityKind Kind,
    string KindLabel,
    StudioActivityStatus Status,
    string Description,
    QuotaType QuotaType,
    decimal Units,
    decimal UnitsRefunded,
    decimal NetUnits,
    decimal BalanceAfter,
    string OperationKey,
    string? ReferenceType,
    string? ReferenceId,
    string? Provider,
    string? Detail,
    Guid? ActorUserId,
    string? ActorName,
    DateTime RequestedAtUtc,
    DateTime? CompletedAtUtc);

public record StudioActivityQuery : PagedRequest
{
    public StudioActivityKind? Kind { get; init; }

    public StudioActivityStatus? Status { get; init; }

    public QuotaType? QuotaType { get; init; }

    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }
}

/// <summary>Totals for one quota type over a period: what was charged, what came back, what it nets to.</summary>
public record StudioUsageTotalDto(QuotaType QuotaType, decimal Charged, decimal Refunded, decimal Net, int Activities, int Blocked);

public record StudioOverviewDto(
    bool Enabled,
    StudioApprovalPolicy ApprovalPolicy,
    IReadOnlyList<QuotaBalanceDto> Balances,
    IReadOnlyList<StudioUsageTotalDto> Last30Days);

public record StudioSettingsDto(StudioApprovalPolicy ApprovalPolicy);

public record UpdateStudioSettingsRequest(StudioApprovalPolicy ApprovalPolicy);

/// <summary>A request to meter one studio step. <see cref="Units"/> null uses the catalog default for the kind.</summary>
public record StudioChargeRequest(
    Guid TenantId,
    StudioActivityKind Kind,
    string OperationKey,
    string Description,
    decimal? Units = null,
    string? ReferenceType = null,
    string? ReferenceId = null,
    string? Provider = null,
    Guid? ActorUserId = null);

/// <summary><see cref="Allowed"/> false means nothing was spent and the work must not run; <see cref="Message"/> is what
/// to tell the tenant. <see cref="AlreadyCharged"/> is a retry of an operation that was charged before.</summary>
public record StudioChargeResult(bool Allowed, Guid ActivityId, bool AlreadyCharged, string? Message);
