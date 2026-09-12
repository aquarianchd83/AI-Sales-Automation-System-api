namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Platform Dashboard's aggregate metrics (spec item #1) - "at a glance" cross-tenant
/// numbers, all computed from local data. <see cref="MrrUsd"/> is a simple sum of active/trialing
/// subscriptions' plan prices, not a true prorated MRR figure. <see cref="EstimatedAiSpendThisMonthUsd"/>
/// carries the same estimate caveat as <see cref="AiSpendEstimator"/>.</summary>
public record PlatformDashboardDto(
    int ActiveTenants,
    int TrialTenants,
    int SuspendedTenants,
    int SignupsThisMonth,
    decimal MrrUsd,
    int MessagesSentThisMonth,
    int AiInteractionsThisMonth,
    decimal EstimatedAiSpendThisMonthUsd,
    int WebhookFailuresLast24h,
    bool SystemHealthy);
