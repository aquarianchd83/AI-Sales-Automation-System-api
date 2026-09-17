namespace WhatsAppSalesAutomation.Application.Tenancy;

/// <summary>One selectable timezone - <see cref="Id"/> is the IANA id actually stored on
/// Tenant.Timezone and passed to TimeZoneInfo.FindSystemTimeZoneById at conversion time.</summary>
public record TimeZoneOption(string Id, string DisplayName, string UtcOffset);

/// <summary>
/// A curated list of common IANA timezone ids, not <c>TimeZoneInfo.GetSystemTimeZones()</c> - that
/// enumeration is platform-dependent (Windows returns native names like "India Standard Time", Linux
/// returns IANA names like "Asia/Kolkata"), exactly the risk <c>DateTimeProvider.cs</c>'s own doc
/// comment already flagged for why IST itself was hardcoded rather than looked up. Keeping the *list*
/// curated (and IANA-only) sidesteps that entirely; the *lookup* at conversion time
/// (<c>TimeZoneInfo.FindSystemTimeZoneById</c>) resolves IANA ids consistently on both Windows and
/// Linux under .NET 6+, so there's no cross-platform risk there.
///
/// Not exhaustive - one or two representative zones per major region, enough to cover where a real
/// customer is likely signing up from. <see cref="DefaultId"/> is this platform's original,
/// still-default assumption (India Standard Time) for a tenant who hasn't set one.
/// </summary>
public static class TimeZoneCatalog
{
    public const string DefaultId = "Asia/Kolkata";

    public static readonly IReadOnlyList<TimeZoneOption> All = new List<TimeZoneOption>
    {
        new("UTC", "UTC", "+00:00"),
        new("America/Los_Angeles", "Pacific Time (US)", "-08:00"),
        new("America/Denver", "Mountain Time (US)", "-07:00"),
        new("America/Chicago", "Central Time (US)", "-06:00"),
        new("America/New_York", "Eastern Time (US)", "-05:00"),
        new("America/Toronto", "Toronto", "-05:00"),
        new("America/Mexico_City", "Mexico City", "-06:00"),
        new("America/Sao_Paulo", "São Paulo", "-03:00"),
        new("Europe/London", "London", "+00:00"),
        new("Europe/Madrid", "Madrid", "+01:00"),
        new("Europe/Paris", "Paris", "+01:00"),
        new("Europe/Berlin", "Berlin", "+01:00"),
        new("Europe/Rome", "Rome", "+01:00"),
        new("Europe/Amsterdam", "Amsterdam", "+01:00"),
        new("Europe/Moscow", "Moscow", "+03:00"),
        new("Africa/Cairo", "Cairo", "+02:00"),
        new("Africa/Johannesburg", "Johannesburg", "+02:00"),
        new("Asia/Dubai", "Dubai", "+04:00"),
        new(DefaultId, "India (Kolkata)", "+05:30"),
        new("Asia/Dhaka", "Dhaka", "+06:00"),
        new("Asia/Bangkok", "Bangkok", "+07:00"),
        new("Asia/Jakarta", "Jakarta", "+07:00"),
        new("Asia/Singapore", "Singapore", "+08:00"),
        new("Asia/Hong_Kong", "Hong Kong", "+08:00"),
        new("Asia/Shanghai", "Shanghai", "+08:00"),
        new("Asia/Tokyo", "Tokyo", "+09:00"),
        new("Australia/Perth", "Perth", "+08:00"),
        new("Australia/Sydney", "Sydney", "+10:00"),
        new("Pacific/Auckland", "Auckland", "+12:00"),
    };

    private static readonly HashSet<string> ValidIds = All.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>True only for an id in this exact curated list - deliberately not a general
    /// TimeZoneInfo.FindSystemTimeZoneById probe, so a stored value is always one this catalog (and
    /// therefore every picker built from it) can actually show back to whoever set it.</summary>
    public static bool IsValidId(string? id) => id is not null && ValidIds.Contains(id);

    /// <summary>The <see cref="TimeZoneInfo"/> for a stored id, never throwing: an unset id, or one the host
    /// can't resolve (corrupt data, or a host missing tz data), degrades to <see cref="DefaultId"/> rather
    /// than taking down whatever was scheduling or reporting against it - the same "corrupt/unusable value
    /// behaves as unset" tolerance AppSettingsStore's own TryUnprotect uses.</summary>
    public static TimeZoneInfo Resolve(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Fall through to the default below.
            }
        }

        return TimeZoneInfo.FindSystemTimeZoneById(DefaultId);
    }
}
