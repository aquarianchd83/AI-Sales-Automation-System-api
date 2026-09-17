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
    decimal EstimatedAiSpendThisMonthUsd,
    string Timezone,
    string? CountryCode,
    // CurrencyCode/CurrencySymbol are this tenant's own, resolved from CountryCode - what the tenant is
    // quoted in on its own screens, so the console shows their spend the way they see it. Platform-wide
    // figures (the dashboard) use the operator's currency instead.
    string CurrencyCode,
    string CurrencySymbol,
    decimal EstimatedAiSpendThisMonthLocal);

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

/// <summary>
/// Operator-initiated tenant creation - the Platform Admin Console's counterpart to the self-serve
/// <c>AuthService.SignUpAsync</c> flow (same shape: a new Tenant on trial plus its first Admin
/// user), for support/sales-assisted onboarding rather than the customer signing themselves up.
/// Unlike self-serve signup, the caller here doesn't log in as the new admin afterwards - the
/// PlatformSuperAdmin sets <see cref="AdminPassword"/> as a temporary password to hand off, the
/// same "set it, then share it securely" convention <c>CreateUserRequest</c> already uses for
/// tenant-scoped user creation.
///
/// Everything after <see cref="AdminPassword"/> is optional - the Business details section of the
/// console's New tenant dialog. CountryCode and Timezone must be supported values when given (Timezone
/// defaults to TimeZoneCatalog.DefaultId like signup); the business fields follow the same rules and
/// normalization as the tenant's own Business Profile (see Tenancy.TenantBusinessDetails).
/// </summary>
public record CreatePlatformTenantRequest(
    string CompanyName,
    string? Slug,
    string AdminFullName,
    string AdminEmail,
    string AdminPassword,
    string? CountryCode = null,
    string? Timezone = null,
    string? ProductName = null,
    string? Industry = null,
    string? BusinessDescription = null,
    string? WebsiteUrl = null,
    string? SupportEmail = null,
    string? SupportPhone = null,
    IReadOnlyList<string>? DomainKeywords = null) : Tenancy.ITenantBusinessDetails;
