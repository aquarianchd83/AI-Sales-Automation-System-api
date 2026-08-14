using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.Services;

public class DateTimeProvider : IDateTimeProvider
{
    /// <summary>India has a single, fixed UTC offset year-round (no DST), so this is safe as a
    /// constant rather than a <c>TimeZoneInfo</c> lookup - which would also risk failing depending on
    /// whether the host OS has "Asia/Kolkata" (Linux/ICU) or "India Standard Time" (Windows) registered.</summary>
    private static readonly TimeSpan IstOffset = new(5, 30, 0);

    public DateTime UtcNow => DateTime.UtcNow;

    public DateTime IstNow => DateTime.SpecifyKind(DateTime.UtcNow + IstOffset, DateTimeKind.Unspecified);
}
