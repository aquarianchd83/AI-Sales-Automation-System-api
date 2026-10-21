using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// One credit spend, from the point of view of "who paid and why": the scenario, the tenant it was for, the payer the platform had set,
/// and the account that was really charged. This is what the Credit Payers screen lists, and what a tenant sees as "covered by the platform".
/// Platform-owned (like <c>AiExecution</c>): <see cref="TenantId"/> only says whose action it was and is never used as a global filter, so
/// the platform screen can read across tenants. A tenant-facing read filters on it explicitly.
/// </summary>
public class CreditUsage : BaseEntity
{
    public Guid TenantId { get; set; }

    public CreditScenario Scenario { get; set; }

    public QuotaType QuotaType { get; set; }

    public decimal Units { get; set; }

    /// <summary>What the scenario's setting said at the time.</summary>
    public CreditPayer ConfiguredPayer { get; set; }

    /// <summary>The account that was really charged. Differs from <see cref="ConfiguredPayer"/> only when the platform wallet could not
    /// cover it and the tenant paid instead (<see cref="FellBackToTenant"/>).</summary>
    public CreditPayer ChargedTo { get; set; }

    public bool FellBackToTenant { get; set; }

    public CreditUsageStatus Status { get; set; }

    /// <summary>The ledger operation key the spend was made under; with the tenant it identifies the spend.</summary>
    public string OperationKey { get; set; } = string.Empty;

    public string? ReferenceType { get; set; }

    public string? ReferenceId { get; set; }

    /// <summary>A short human description of the action, e.g. the template category or the AI task.</summary>
    public string? Description { get; set; }

    public DateTime OccurredAtUtc { get; set; }

    public DateTime? SettledAtUtc { get; set; }

    /// <summary>For a goodwill redemption: who gave the credits back and why.</summary>
    public Guid? SettledByUserId { get; set; }

    public string? SettlementNote { get; set; }
}
