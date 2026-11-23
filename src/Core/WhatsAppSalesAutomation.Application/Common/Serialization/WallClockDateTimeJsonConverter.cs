using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhatsAppSalesAutomation.Application.Common.Serialization;

/// <summary>
/// For the few fields that are a tenant's own wall-clock time rather than an instant - <c>Campaign.ScheduledStartAt</c> and
/// <c>AutoCampaignEnrollment.ExecutionDateLocal</c>. The API writes every other <see cref="DateTime"/> as UTC with a trailing Z
/// (UtcDateTimeJsonConverter); a wall-clock value must not get one, or the browser shifts "10:00 in the tenant's zone" by the
/// viewer's offset. Writing leaves the zone off; reading keeps the literal digits and ignores any offset or Z, as the DTOs document.
/// Put it on the property: <c>[property: JsonConverter(typeof(WallClockDateTimeJsonConverter))]</c>, which wins over the global
/// UTC converter.
/// </summary>
public sealed class WallClockDateTimeJsonConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        // AssumeUniversal only keeps parsing independent of the server's own zone; .DateTime is the digits as written either way.
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value))
            throw new JsonException($"'{text}' is not a valid date and time.");
        return DateTime.SpecifyKind(value.DateTime, DateTimeKind.Unspecified);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
}

/// <summary>The nullable form of <see cref="WallClockDateTimeJsonConverter"/>.</summary>
public sealed class WallClockNullableDateTimeJsonConverter : JsonConverter<DateTime?>
{
    private readonly WallClockDateTimeJsonConverter _inner = new();

    public override bool HandleNull => true;

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : _inner.Read(ref reader, typeof(DateTime), options);

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            _inner.Write(writer, value.Value, options);
    }
}
