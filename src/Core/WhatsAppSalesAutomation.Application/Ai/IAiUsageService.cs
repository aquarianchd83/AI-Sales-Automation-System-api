using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Ai;

/// <summary>
/// The gate every metered AI operation passes through: subscription state, then credit, then an audit row.
/// Authorize either spends the credit and opens a transaction, or refuses and records why. The caller then runs the
/// provider call and closes the transaction with Complete or - when the platform or provider failed - Fail, which gives
/// the credit back. The tenant never chooses or sees a provider: the call itself goes through the platform's one AI
/// configuration.
/// </summary>
public interface IAiUsageService
{
    Task<AiAuthorization> AuthorizeAsync(Guid tenantId, string operation, string referenceId, CancellationToken cancellationToken = default);

    Task CompleteAsync(Guid transactionId, string provider, string model, int? promptTokens, int? completionTokens, CancellationToken cancellationToken = default);

    /// <summary>The platform/provider failed: marks the transaction failed and releases the credit it reserved.</summary>
    Task FailAsync(Guid transactionId, string reason, string? provider, string? model, CancellationToken cancellationToken = default);

    Task<PagedResult<AiTransactionDto>> GetHistoryAsync(Guid tenantId, AiTransactionQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Message is what to show the tenant when Allowed is false.</summary>
public record AiAuthorization(bool Allowed, Guid? TransactionId, AiDenialReason Reason, string? Message);

public record AiTransactionDto(
    Guid Id,
    string Operation,
    AiUsageSource Source,
    AiTransactionStatus Status,
    AiDenialReason DenialReason,
    string Provider,
    string Model,
    decimal CreditsBefore,
    decimal CreditsConsumed,
    decimal CreditsAfter,
    decimal CreditsRefunded,
    string? FailureReason,
    DateTime RequestedAtUtc,
    DateTime? CompletedAtUtc);

public record AiTransactionQuery : PagedRequest
{
    public AiTransactionStatus? Status { get; init; }
}
