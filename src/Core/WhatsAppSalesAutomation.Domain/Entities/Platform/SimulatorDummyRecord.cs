using WhatsAppSalesAutomation.Domain.Common;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Domain.Entities.Platform;

/// <summary>
/// One piece of dummy data the simulator uses instead of a real provider: a lead business, a canned AI reply, intent keywords, a
/// predefined task answer, a DND number or a simulator switch. Platform-wide (not tenant owned) and edited by the PlatformSuperAdmin
/// on the Simulator dummy data page, so what the simulator does is under their control rather than hardcoded.
///
/// One table for all kinds: <see cref="Name"/> is the key for keyed kinds (AiReply, IntentKeywords, TaskAnswer, Setting), the
/// business name for LeadBusiness and the number for DndNumber; <see cref="Value"/> is the text. The business columns are used by
/// LeadBusiness only.
/// </summary>
public class SimulatorDummyRecord : BaseEntity
{
    public SimulatorDataCategory Category { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Value { get; set; }

    public string? BusinessType { get; set; }

    public string? ContactPerson { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Website { get; set; }

    public int? LeadScore { get; set; }

    public SimulatorLeadVariant? Variant { get; set; }

    /// <summary>An inactive row is kept but the simulator ignores it.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Lower comes first: lead businesses are handed out in this order.</summary>
    public int DisplayOrder { get; set; }
}
