namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>Coarse, human-facing bucket derived from <c>Lead.ScoreNumeric</c> - shared by
/// <c>Lead.Score</c> and <c>Conversation.LastLeadScore</c> so the inbox and the pipeline board agree on
/// the same three-way read of "how hot is this lead" without either owning the numeric thresholds.</summary>
public enum LeadScoreBand
{
    Cold = 0,
    Warm = 1,
    Hot = 2
}
