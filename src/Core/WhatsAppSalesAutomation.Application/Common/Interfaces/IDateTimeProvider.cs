namespace WhatsAppSalesAutomation.Application.Common.Interfaces;

/// <summary>Testable indirection over <see cref="DateTime.UtcNow"/>.</summary>
public interface IDateTimeProvider
{
    DateTime UtcNow { get; }

    /// <summary>
    /// Current wall-clock time in India Standard Time (UTC+5:30, no DST - the offset is fixed
    /// year-round). <see cref="DateTime"/> cannot carry a non-UTC/non-Local offset, so the returned
    /// value has <see cref="DateTimeKind.Unspecified"/> to signal "these digits are IST, not UTC" -
    /// never call <c>.ToUniversalTime()</c> on it, that would shift it again by another 5:30.
    ///
    /// Originally used directly for <c>Campaign.ScheduledStartAt</c>'s "is it due yet" comparisons,
    /// back when this platform's entire customer base was India-only. Per-tenant timezones
    /// generalized that: <c>ITenantTimeZoneProvider.GetLocalNowAsync</c> is what those comparisons
    /// use now, falling back to this exact value (via Tenancy.TimeZoneCatalog.DefaultId) only for a
    /// tenant who hasn't set their own timezone - not called directly for that purpose anymore.
    /// </summary>
    DateTime IstNow { get; }
}
