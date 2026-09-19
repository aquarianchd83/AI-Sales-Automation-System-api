using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Application.Quota;

/// <summary>
/// What the send, AI and discovery paths call before doing something that costs money. A thin,
/// domain-shaped layer over <see cref="IQuotaLedgerService"/>: it knows the WhatsApp category weights and
/// the operation-key conventions, so those paths just ask "may I?" and never build a ledger request.
/// Every "try" is refuse-don't-throw: running out of quota is normal, not exceptional.
/// </summary>
public interface IQuotaGate
{
    /// <summary>Spends the category's weight of WhatsApp quota for one billable template send. False, with
    /// nothing spent, when the balance can't cover it. Idempotent per <paramref name="operationKey"/>.</summary>
    Task<bool> TryConsumeWhatsAppTemplateAsync(Guid tenantId, TemplateCategory category, string operationKey, string referenceId, CancellationToken cancellationToken = default);

    /// <summary>Spends one AI conversation. False, with nothing spent, when none are left.</summary>
    Task<bool> TryConsumeAiConversationAsync(Guid tenantId, string operationKey, string referenceId, CancellationToken cancellationToken = default);

    /// <summary>Spends <paramref name="candidates"/> lead candidates, or as many as are left. Returns what was
    /// actually spent.</summary>
    Task<decimal> ConsumeLeadCandidatesAsync(Guid tenantId, decimal candidates, string operationKey, string referenceId, string note, CancellationToken cancellationToken = default);

    /// <summary>Gives back everything the operation spent - a send that failed before delivery.</summary>
    Task ReleaseAsync(Guid tenantId, string operationKey, CancellationToken cancellationToken = default);

    /// <summary>Gives a brand-new tenant its free trial quota (Billing:Trial), expiring when the trial does.
    /// Idempotent.</summary>
    Task GrantTrialAsync(Guid tenantId, DateTime trialEndsAtUtc, CancellationToken cancellationToken = default);

    Task<decimal> GetAvailableAsync(Guid tenantId, QuotaType quotaType, CancellationToken cancellationToken = default);
}
