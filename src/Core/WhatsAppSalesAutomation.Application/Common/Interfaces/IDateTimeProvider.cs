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
    /// Used only for <c>Campaign.ScheduledStartAt</c>, which is deliberately pinned to IST rather
    /// than left ambiguous - see the doc comment on that property for why.
    /// </summary>
    DateTime IstNow { get; }
}
