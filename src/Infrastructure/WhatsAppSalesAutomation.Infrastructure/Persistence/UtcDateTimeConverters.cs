using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence;

/// <summary>
/// Every timestamp column holds UTC, but SQL Server's datetime2 carries no zone, so EF Core hands it back with Kind Unspecified.
/// That made the value's meaning depend on who looked at it: System.Text.Json writes an Unspecified value with no Z (the browser
/// then reads UTC digits as its own local time), and TimeZoneInfo.ConvertTime treats Unspecified as the server's local zone.
/// Applied to every DateTime / DateTime? property by <see cref="ApplicationDbContext"/>'s conventions, this marks a value read
/// from the database as Utc. Writing stores the digits exactly as given, whatever their Kind, as EF did before: a tenant-local
/// value can carry Kind Local when the server shares the tenant's zone, and shifting it would move it by the offset.
/// The column type does not change, so this needs no migration.
/// </summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            value => value,
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
    {
    }
}

/// <summary>
/// The opt-out from <see cref="UtcDateTimeConverter"/> for a column that holds a tenant's own wall-clock time, not an instant
/// (<c>Campaign.ScheduledStartAt</c>, <c>AutoCampaignEnrollment.ExecutionDateLocal</c>, the Lead Discovery <c>ProcessingDate</c>s). Those digits are in the tenant's zone, so
/// they are read back as Unspecified - never labelled UTC - and stored exactly as given, whatever Kind they arrive with.
/// </summary>
public sealed class WallClockDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public WallClockDateTimeConverter()
        : base(
            value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
            value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified))
    {
    }
}
