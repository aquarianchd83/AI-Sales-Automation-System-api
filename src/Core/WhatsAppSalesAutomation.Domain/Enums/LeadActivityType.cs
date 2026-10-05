namespace WhatsAppSalesAutomation.Domain.Enums;

/// <summary>What kind of change a <c>LeadActivity</c> row records.</summary>
public enum LeadActivityType
{
    ScoreChanged = 0,
    StageChanged = 1,
    Note = 2,
    AssignmentChanged = 3,
    FollowUpScheduled = 4,
    FollowUpSent = 5,
    FollowUpCancelled = 6,
    FollowUpSuggested = 7
}
