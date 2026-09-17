namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>
/// "This month" as one tenant experiences it. A tenant in Asia/Kolkata starts its month 5.5 hours before a
/// tenant in UTC does, so a figure a tenant is shown about its own spending should be measured from the
/// tenant's own local month, not from the platform's.
///
/// Deliberately not applied to the plan's message allowance: that is enforced on the UTC calendar month by
/// PlanLimitsService, and a quota displayed on a different window from the one enforced would be worse than
/// one displayed on an unfamiliar window. If the allowance ever moves to local months, it moves here too.
/// </summary>
public static class TenantMonth
{
    /// <summary>The instant the tenant's current calendar month began, as UTC. An unknown or unset timezone
    /// falls back to the platform default, the same tolerance TenantTimeZoneProvider applies.</summary>
    public static DateTime StartUtc(string? timeZoneId, DateTime utcNow)
    {
        var timeZone = TimeZoneCatalog.Resolve(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        var localMonthStart = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);

        // A handful of zones move their clocks at midnight (e.g. America/Santiago), so the first instant of a
        // month can be a local time that never happened. Stepping forward an hour lands in the same day and
        // keeps the window a real instant rather than throwing.
        if (timeZone.IsInvalidTime(localMonthStart))
            localMonthStart = localMonthStart.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(localMonthStart, timeZone);
    }
}
