using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>One scenario on the Credit Payers screen: who pays now, and what the last 30 days looked like. <paramref name="ProviderCostLast30DaysUsd"/>
/// is the AI provider's estimated bill for the scenario - what it really costs the platform, whoever pays the credits.</summary>
public record CreditScenarioDto(
    CreditScenario Scenario,
    string Title,
    string Area,
    string Description,
    QuotaType QuotaType,
    CreditPayer Payer,
    CreditPayer DefaultPayer,
    bool UnitsConfigurable,
    // What one unit costs in credits (for product knowledge, one action): the platform's Credit costs setting for the scenario.
    decimal? UnitsPerAction,
    decimal PlatformBalance,
    int ActionsLast30Days,
    decimal UnitsChargedToPlatformLast30Days,
    decimal UnitsChargedToTenantsLast30Days,
    int FallbackCountLast30Days,
    decimal? ProviderCostLast30DaysUsd,
    DateTime? ChangedAtUtc,
    string UnitLabel = "");

public record UpdateCreditPayerRequest(CreditPayer Payer, decimal? UnitsPerAction = null);

public record PlatformCreditWalletDto(QuotaType QuotaType, decimal Balance);

public record AdjustPlatformWalletRequest(QuotaType QuotaType, decimal UnitsDelta, string Reason);

public record PlatformCreditEntryDto(
    Guid Id,
    QuotaType QuotaType,
    PlatformCreditEntryType EntryType,
    decimal UnitsDelta,
    decimal BalanceAfter,
    Guid? TenantId,
    string? TenantName,
    string? Note,
    DateTime OccurredAtUtc);

public record PlatformCreditEntryQuery : PagedRequest
{
    public QuotaType? QuotaType { get; init; }
}

public record CreditUsageDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    CreditScenario Scenario,
    string ScenarioTitle,
    QuotaType QuotaType,
    decimal Units,
    CreditPayer ConfiguredPayer,
    CreditPayer ChargedTo,
    bool FellBackToTenant,
    CreditUsageStatus Status,
    string? Description,
    string? ReferenceType,
    string? ReferenceId,
    DateTime OccurredAtUtc,
    DateTime? SettledAtUtc,
    string? SettlementNote);

public record CreditUsageQuery : PagedRequest
{
    public CreditScenario? Scenario { get; init; }

    public CreditPayer? ChargedTo { get; init; }

    public CreditUsageStatus? Status { get; init; }

    public Guid? TenantId { get; init; }

    public bool? FellBackToTenant { get; init; }

    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }
}

public record RedeemCreditUsageRequest(string Note);

/// <summary>What a tenant sees of its own credit usage. The platform's wallet is not shown, only whether the platform covered the action.</summary>
public record TenantCreditUsageDto(
    Guid Id,
    CreditScenario Scenario,
    string ScenarioTitle,
    QuotaType QuotaType,
    decimal Units,
    bool CoveredByPlatform,
    bool Refunded,
    string? Description,
    DateTime OccurredAtUtc);

public record TenantCreditUsageQuery : PagedRequest
{
    public CreditScenario? Scenario { get; init; }

    public bool? CoveredByPlatform { get; init; }

    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }
}
