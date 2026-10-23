using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Billing;

/// <summary>
/// Who pays for one <see cref="CreditScenario"/>, platform-wide. Platform-owned: it is not tied to any tenant. A scenario with no row
/// uses the defaults in <c>CreditScenarioCatalog</c>, so nothing needs seeding.
/// </summary>
public class CreditScenarioSetting : BaseEntity
{
    public CreditScenario Scenario { get; set; }

    public CreditPayer Payer { get; set; }

    /// <summary>Credits one action costs, for a scenario whose cost is not set by the code that performs it (product knowledge).
    /// Zero means the action is free. Ignored by scenarios that size their own spend (a template's weight, a candidate count).</summary>
    public decimal? UnitsPerAction { get; set; }

    /// <summary>Credits one unit of the action costs: a WhatsApp message unit, an AI reply, a lead candidate, a Content Studio step. Null uses the catalog default.
    /// For scenarios whose cost is set per action (<see cref="UnitsPerAction"/>) this is ignored.</summary>
    public decimal? CreditsPerUnit { get; set; }

    public Guid? UpdatedByUserId { get; set; }
}
