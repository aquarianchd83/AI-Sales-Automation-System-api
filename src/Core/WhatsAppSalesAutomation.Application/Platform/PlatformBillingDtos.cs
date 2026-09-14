using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

/// <summary>Platform-only view of the plan catalog - unlike the tenant-facing <c>PlanDto</c>
/// (Application.Billing), this includes <see cref="IsActive"/> since a PlatformSuperAdmin manages the
/// catalog itself rather than just picking from it.</summary>
public record PlatformPlanDto(
    Guid Id,
    string Code,
    string Name,
    int MaxUsers,
    int MaxMessagesPerMonth,
    int MaxCampaigns,
    int MaxKnowledgeBaseArticles,
    int PriceMonthlyCents,
    bool IsActive);

/// <summary>One row of the Subscriptions & Billing screen (spec item #3). <see cref="HasFailedPayment"/>
/// is derived from <see cref="SubscriptionStatus.PastDue"/> - this system keeps no per-invoice ledger,
/// so "failed payments" here means "this tenant's subscription is currently past due," not a
/// per-invoice history (payments themselves are simulated for now - see
/// Application.Billing.IBillingService's own doc comment).</summary>
public record PlatformSubscriptionListItemDto(
    Guid TenantId,
    string TenantName,
    string? PlanName,
    SubscriptionStatus? Status,
    DateTime? CurrentPeriodEndUtc,
    bool HasFailedPayment);

public record PlatformSubscriptionQuery : PagedRequest
{
    public SubscriptionStatus? Status { get; init; }
}

/// <summary>Body of POST the plan catalog endpoint. <see cref="Code"/> is immutable once a plan
/// exists (see Plan.Code's own doc comment) - there is no update path for it, only creation.</summary>
public record CreatePlanRequest(
    string Code,
    string Name,
    int MaxUsers,
    int MaxMessagesPerMonth,
    int MaxCampaigns,
    int MaxKnowledgeBaseArticles,
    int PriceMonthlyCents);

/// <summary>Body of PUT one plan. <see cref="IsActive"/> is how a plan is both retired ("Delete" in
/// the admin UI sets it false) and un-retired - there is no separate reactivate endpoint.</summary>
public record UpdatePlanRequest(
    string Name,
    int MaxUsers,
    int MaxMessagesPerMonth,
    int MaxCampaigns,
    int MaxKnowledgeBaseArticles,
    int PriceMonthlyCents,
    bool IsActive);
