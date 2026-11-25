namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>The limits on consent reminders. Constants on purpose: they are the floor on how often a customer who has not answered is texted.</summary>
public static class ConsentReminderPolicy
{
    /// <summary>Reminders only go from this local hour (tenant timezone) ...</summary>
    public const int SendWindowStartHour = 9;

    /// <summary>... until this one, exclusive.</summary>
    public const int SendWindowEndHour = 20;

    /// <summary>The first reminder is never set later than this many days after the invitation.</summary>
    public const int MaxReminderDay = 13;
}
