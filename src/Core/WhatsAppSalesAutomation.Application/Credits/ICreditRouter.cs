using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Credits;

/// <summary>
/// Decides whose balance a credit spend comes out of, and keeps the record of who paid. A spend whose scenario is set to the platform comes out
/// of the platform wallet; when that cannot cover it the tenant pays instead, so a customer is never left unanswered because the platform ran
/// dry. Everything else is the tenant ledger exactly as before.
/// </summary>
public interface ICreditRouter
{
    /// <summary>The routed version of <see cref="IQuotaLedgerService.ConsumeAsync"/>. Idempotent per tenant and operation key.</summary>
    Task<ConsumeResult> ConsumeAsync(ConsumeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gives back what a spend took, from whichever account paid it. Idempotent. A spend a platform admin has already redeemed is left alone.</summary>
    Task<bool> ReverseAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken = default);

    /// <summary>Charges one action of a scenario whose cost the platform sets (product knowledge): the configured units, from the configured
    /// payer. A free scenario (zero units) charges nothing and records nothing. Never throws for lack of credit: the work has already been
    /// done by then, so a spend nobody can cover is recorded as unpaid.</summary>
    Task ChargeActionAsync(Guid tenantId, CreditScenario scenario, string operationKey, string description, CancellationToken cancellationToken = default);
}
