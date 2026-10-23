using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Quota;

/// <summary>One live bucket inside a balance - what "included until 30 Sep" and "credits until next
/// August" are made of on the tenant's own screen.</summary>
public record QuotaGrantDto(Guid Id, QuotaGrantOrigin Origin, decimal UnitsGranted, decimal UnitsRemaining, DateTime ExpiresAtUtc);

/// <summary><see cref="Capacity"/> is everything the live grants started with, including ones already used up, so a
/// screen can tell "used all of it" (Balance 0, Capacity above 0) from "never had any" (both 0).
/// <see cref="Grants"/> lists only the ones with units left.</summary>
public record QuotaBalanceDto(QuotaType QuotaType, decimal Balance, decimal Capacity, IReadOnlyList<QuotaGrantDto> Grants);

public record QuotaLedgerEntryDto(
    Guid Id,
    QuotaType QuotaType,
    PlatformFeature? Feature,
    QuotaEntryType EntryType,
    decimal UnitsDelta,
    decimal BalanceAfter,
    Guid? GrantId,
    string? ReferenceType,
    string? ReferenceId,
    string? Note,
    DateTime OccurredAtUtc);

public record QuotaLedgerQuery : Common.Models.PagedRequest
{
    public QuotaType? QuotaType { get; init; }

    /// <summary>Only this feature's entries: the per-feature ledger.</summary>
    public PlatformFeature? Feature { get; init; }
}

/// <summary>A request to spend units. <see cref="OperationKey"/> must be unique per real-world event
/// (a message id, an AI interaction id, a run id): retrying the same key never charges twice.
/// <see cref="AllowPartial"/> spends whatever is available instead of refusing when the balance is
/// short - for work that can shrink (a discovery run with a smaller batch), never for a single send.
/// <see cref="Scenario"/> names which credit scenario the spend is, so the platform's payer setting for it applies; left null, it is worked out
/// from <see cref="ReferenceType"/> (see <c>CreditScenarioCatalog.Classify</c>).</summary>
public record ConsumeRequest(
    Guid TenantId,
    QuotaType QuotaType,
    decimal Units,
    string OperationKey,
    string ReferenceType,
    string ReferenceId,
    string? Note = null,
    bool AllowPartial = false,
    CreditScenario? Scenario = null,
    PlatformFeature? Feature = null,
    bool UnitsAreCredits = false);

/// <summary>How much of what one payment bought is still unspent - what refund eligibility is judged on.
/// <see cref="UnitsRemaining"/> is zero for a grant that has expired or is already held.</summary>
public record PaymentGrantUsage(QuotaType QuotaType, QuotaGrantOrigin Origin, decimal UnitsGranted, decimal UnitsRemaining, DateTime ExpiresAtUtc);

/// <summary><see cref="Consumed"/> is what was actually taken - less than the request only for
/// AllowPartial, and zero when nothing was available. <see cref="Sufficient"/> is false whenever the
/// request could not be met in full.</summary>
/// <param name="CreditsSpent">The credits that really left the wallet. <paramref name="Consumed"/> is in the units the caller asked in (replies, messages, candidates);
/// they differ when an action costs more than one credit. Null from the plain ledger, which only knows credits.</param>
public record ConsumeResult(decimal Requested, decimal Consumed, decimal BalanceAfter, bool Sufficient, bool AlreadyApplied, decimal? CreditsSpent = null);
