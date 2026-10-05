namespace WhatsAppSalesAutomation.Application.Leads.FollowUps;

/// <summary>
/// The limits that keep "follow up later" from turning into pestering. Constants rather than tenant
/// settings on purpose: these are the floor on how often a customer who said "not now" can be messaged, and a
/// floor a tenant can lower is not a floor.
/// </summary>
public static class LeadFollowUpPolicy
{
    /// <summary>The longest wait a follow-up can be scheduled for.</summary>
    public const int MaxMonths = 12;

    /// <summary>A custom date must be at least this far ahead - a "follow up later" a few days out is just a
    /// second message to someone who has not had time to change their answer.</summary>
    public const int MinDaysAhead = 7;

    /// <summary>Follow-ups actually sent to one lead, in total. A customer who has not come back after three
    /// reminders has given their answer.</summary>
    public const int MaxSentPerLead = 3;

    /// <summary>Messages only go out from this local hour (tenant timezone) ...</summary>
    public const int SendWindowStartHour = 9;

    /// <summary>... until this one, exclusive - so nothing lands on someone's phone at night.</summary>
    public const int SendWindowEndHour = 20;

    /// <summary>A due follow-up waits if the customer was messaged by anything else (a campaign step, an agent
    /// reply) within this many days, so two messages never arrive back to back.</summary>
    public const int QuietDaysAfterAnyMessage = 7;
}
