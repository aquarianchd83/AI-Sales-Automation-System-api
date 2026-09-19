namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>One tenant's row on the Usage & Quotas screen (spec item #4) - this calendar month only,
/// same "current month" window <c>PlanLimitsService</c> uses for its own message-quota guard.
/// <see cref="EstimatedAiSpendThisMonthUsd"/> is an estimate - see <see cref="AiSpendEstimator"/>'s
/// own doc comment for why it can't be exact. A null *Limit means the tenant has no active plan
/// (still on trial, or between plans) and so no quota applies - never treated as "unlimited breach."
///
/// The three spend figures are estimates from three different sources and are not interchangeable:
/// <see cref="EstimatedAiSpendThisMonthUsd"/> is conversational AI (see <see cref="AiSpendEstimator"/>),
/// <see cref="EstimatedWhatsAppSpendThisMonthUsd"/> is message sending priced on read (see
/// Billing.IWhatsAppSpendService), and <see cref="EstimatedLeadDiscoverySpendThisMonthUsd"/> is the cost each
/// discovery run was priced at when it ran. <see cref="EstimatedTotalSpendThisMonthUsd"/> is their sum - what
/// this tenant costs the platform to serve this month, excluding what they pay for their plan.
///
/// The spend figures are measured from <see cref="SpendPeriodStartUtc"/> - the start of the month in the
/// TENANT's own timezone (see Tenancy.TenantMonth) - so they match what that tenant sees on its own Settings
/// page. The quota fields above keep the UTC month, because that is the window PlanLimitsService enforces
/// the allowance on; the two windows differ by at most a day, around the turn of a month.
///
/// Each row is about one tenant, so the *Local amounts are quoted in THAT TENANT's currency (from its own
/// country), matching what the tenant is quoted on its own screens. That means a column can mix currencies
/// and its values are not comparable across rows - the *Usd figures are what to compare or total. The
/// platform-wide figures on the dashboard use the operator's own currency instead.</summary>
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
    decimal EstimatedAiSpendThisMonthUsd,
    int BillableWhatsAppMessagesThisMonth,
    decimal EstimatedWhatsAppSpendThisMonthUsd,
    int LeadDiscoveryRunsThisMonth,
    int LeadDiscoveryLeadsThisMonth,
    decimal EstimatedLeadDiscoverySpendThisMonthUsd,
    decimal EstimatedTotalSpendThisMonthUsd,
    DateTime SpendPeriodStartUtc,
    string CurrencyCode,
    string CurrencySymbol,
    decimal EstimatedAiSpendThisMonthLocal,
    decimal EstimatedWhatsAppSpendThisMonthLocal,
    decimal EstimatedLeadDiscoverySpendThisMonthLocal,
    decimal EstimatedTotalSpendThisMonthLocal);
