using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Platform;

public record PlatformTenantListItemDto(
    Guid Id,
    string Name,
    string Slug,
    TenantStatus Status,
    string? PlanName,
    int UserCount,
    DateTime CreatedAt,
    DateTime? TrialEndsAtUtc);

/// <summary>Search matches Name/Slug (PagedRequest.Search).</summary>
public record PlatformTenantQuery : PagedRequest
{
    public TenantStatus? Status { get; init; }
}

public record PlatformTenantDetailDto(
    Guid Id,
    string Name,
    string Slug,
    TenantStatus Status,
    DateTime CreatedAt,
    DateTime? TrialEndsAtUtc,
    Guid? OwnerUserId,
    string? OwnerEmail,
    int UserCount,
    string? PlanName,
    SubscriptionStatus? SubscriptionStatus,
    DateTime? CurrentPeriodEndUtc,
    bool WhatsAppConnected,
    int MessagesSentThisMonth,
    int? MaxMessagesPerMonth,
    int AiInteractionsThisMonth,
    decimal EstimatedAiSpendThisMonthUsd);

public record ImpersonationSessionDto(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    Guid ImpersonatedUserId,
    string ImpersonatedUserEmail,
    Guid TenantId,
    string TenantName);

/// <summary>Force-sets a tenant's plan without going through Stripe Checkout - e.g. a manually
/// negotiated enterprise deal, or support compensating a customer. Does not touch Stripe at all;
/// StripeWebhookHandler can still overwrite this the next time a real Stripe event for this tenant
/// arrives, so this is "what we show/enforce right now," not a permanent override of billing truth.</summary>
public record OverrideTenantPlanRequest(Guid PlanId);
