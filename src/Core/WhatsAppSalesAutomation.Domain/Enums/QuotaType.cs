namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>The prepaid, metered resources a tenant buys. Stored as a string (see the EF
/// configurations) so reordering or extending this never renumbers existing rows.</summary>
public enum QuotaType
{
    /// <summary>Billable WhatsApp template sends, weighted by template category - see
    /// the per-category message prices (Application.Quota.WhatsAppQuotaWeights). A pooled quota rather than one per category.</summary>
    WhatsAppMessages = 0,

    /// <summary>One AI reply interaction.</summary>
    AiConversations = 1,

    /// <summary>One lead-discovery candidate evaluated - fresh, duplicate or rejected alike, because the
    /// provider bills the research whichever way a candidate turns out.</summary>
    LeadCandidates = 2,

    /// <summary>One AI Content Studio generation step that costs the platform money: topic ideas, a script, a
    /// scene breakdown, a voice-over, a generated illustration. Appended after the original three so no stored
    /// value changes meaning.</summary>
    ContentGenerations = 3,

    /// <summary>One finished video rendered by the hosted render service, which bills per rendered video.</summary>
    VideoRenders = 4
}
