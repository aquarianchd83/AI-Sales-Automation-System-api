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
}

/// <summary>A request to spend units. <see cref="OperationKey"/> must be unique per real-world event
/// (a message id, an AI interaction id, a run id): retrying the same key never charges twice.
/// <see cref="AllowPartial"/> spends whatever is available instead of refusing when the balance is
/// short - for work that can shrink (a discovery run with a smaller batch), never for a single send.</summary>
public record ConsumeRequest(
    Guid TenantId,
    QuotaType QuotaType,
    decimal Units,
    string OperationKey,
    string ReferenceType,
    string ReferenceId,
    string? Note = null,
    bool AllowPartial = false);

/// <summary>How much of what one payment bought is still unspent - what refund eligibility is judged on.
/// <see cref="UnitsRemaining"/> is zero for a grant that has expired or is already held.</summary>
public record PaymentGrantUsage(QuotaType QuotaType, QuotaGrantOrigin Origin, decimal UnitsGranted, decimal UnitsRemaining, DateTime ExpiresAtUtc);

/// <summary><see cref="Consumed"/> is what was actually taken - less than the request only for
/// AllowPartial, and zero when nothing was available. <see cref="Sufficient"/> is false whenever the
/// request could not be met in full.</summary>
public record ConsumeResult(decimal Requested, decimal Consumed, decimal BalanceAfter, bool Sufficient, bool AlreadyApplied);
