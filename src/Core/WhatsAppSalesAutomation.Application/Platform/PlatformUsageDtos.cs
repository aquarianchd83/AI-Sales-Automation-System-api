namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One tenant's row on the Usage & Quotas screen (spec item #4) - this calendar month only,
/// same "current month" window <c>PlanLimitsService</c> uses for its own message-quota guard.
/// <see cref="EstimatedAiSpendThisMonthUsd"/> is an estimate - see <see cref="AiSpendEstimator"/>'s
/// own doc comment for why it can't be exact. A null *Limit means the tenant has no active plan
/// (still on trial, or between plans) and so no quota applies - never treated as "unlimited breach."</summary>
public record PlatformTenantUsageDto(
    Guid TenantId,
    string TenantName,
    int MessagesSentThisMonth,
    int? MessageLimit,
    bool MessageQuotaBreached,
    int UserCount,
    int? UserLimit,
    bool UserQuotaBreached,
    int AiInteractionsThisMonth,
    decimal EstimatedAiSpendThisMonthUsd);
