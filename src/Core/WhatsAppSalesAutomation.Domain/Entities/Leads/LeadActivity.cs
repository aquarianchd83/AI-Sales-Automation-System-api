using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Leads;

/// <summary>Append-only history entry for a Lead - every score change, stage change, note, or
/// reassignment. <see cref="CreatedBy"/> is null for AI/system-driven changes (e.g. an automatic score
/// recompute after an AiInteraction) and set for a human agent's manual edit.</summary>
public class LeadActivity : BaseEntity
{
    public Guid LeadId { get; set; }

    public LeadActivityType ActivityType { get; set; }

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? Note { get; set; }

    /// <summary>Null = system/AI. Set = the agent who made this change manually.</summary>
    public Guid? CreatedBy { get; set; }
}
