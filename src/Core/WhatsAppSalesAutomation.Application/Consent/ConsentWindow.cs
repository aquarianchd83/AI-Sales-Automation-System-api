namespace WhatsAppSalesAutomation.Application.Consent;

/// <summary>
/// How long a customer has to answer the consent SMS, and when the reminders may go. The consent link never outlives the campaign it belongs
/// to: a campaign that closes after 6 days gives its customers 6 days to say yes, not the default 14, and nothing - no reminder, no date shown -
/// reaches past that. Without a campaign the default applies.
/// </summary>
public static class ConsentWindow
{
    /// <summary>The window in days: the default, or the campaign's own length when that is shorter. <paramref name="activeStepDelays"/> are the
    /// delays (days after the previous step) of the campaign's active steps; their sum is when its last follow-up falls due.</summary>
    public static int Days(int defaultDays, IEnumerable<int>? activeStepDelays)
    {
        var campaignDays = activeStepDelays?.Sum() ?? 0;
        return campaignDays > 0 ? Math.Min(defaultDays, campaignDays) : defaultDays;
    }

    /// <summary>The days a reminder may be set to inside a window: from 1 up to the day before it closes.</summary>
    public static int LastReminderDay(int windowDays) => Math.Max(windowDays - 1, 0);

    /// <summary>Whether a reminder due <paramref name="dueAfterDays"/> after the invitation still falls inside a window of <paramref name="windowDays"/>.</summary>
    public static bool Fits(int dueAfterDays, int windowDays) => dueAfterDays >= 1 && dueAfterDays <= LastReminderDay(windowDays);
}
