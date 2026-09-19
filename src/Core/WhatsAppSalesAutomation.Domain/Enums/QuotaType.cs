namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>The three prepaid, metered resources a tenant buys. Stored as a string (see the EF
/// configurations) so reordering or extending this never renumbers existing rows.</summary>
public enum QuotaType
{
    /// <summary>Billable WhatsApp template sends, weighted by template category - see
    /// WhatsAppQuotaWeightOptions. A pooled quota rather than one per category.</summary>
    WhatsAppMessages = 0,

    /// <summary>One AI reply interaction.</summary>
    AiConversations = 1,

    /// <summary>One lead-discovery candidate evaluated - fresh, duplicate or rejected alike, because the
    /// provider bills the research whichever way a candidate turns out.</summary>
    LeadCandidates = 2
}
