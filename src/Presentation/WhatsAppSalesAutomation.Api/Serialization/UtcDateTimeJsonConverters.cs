using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace WhatsAppSalesAutomation.Api.Serialization;

/// <summary>
/// Every timestamp this platform stores is UTC. EF Core reads a datetime column back with Kind Unspecified, and System.Text.Json writes such a value with
/// no zone - "2026-10-20T07:30:00" - which a browser then takes as ITS local time and shows shifted by the zone offset. This writes every
/// <see cref="DateTime"/> as UTC with a trailing "Z" (an Unspecified value is taken as UTC, a Local one is converted), so the client can show it
/// in the user's or tenant's timezone. Reading accepts a zone, or none (taken as UTC).
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value))
            throw new JsonException($"'{text}' is not a valid date and time.");
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToUtc(value).ToString(Format, CultureInfo.InvariantCulture));

    internal static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public sealed class UtcNullableDateTimeJsonConverter : JsonConverter<DateTime?>
{
    private readonly UtcDateTimeJsonConverter _inner = new();

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

public static class UtcDateTimeJsonExtensions
{
    /// <summary>Makes every controller write UTC timestamps with a trailing Z. See <see cref="UtcDateTimeJsonConverter"/>.</summary>
    public static IMvcBuilder AddUtcDateTimeJson(this IMvcBuilder builder) =>
        builder.AddJsonOptions(json =>
        {
            json.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
            json.JsonSerializerOptions.Converters.Add(new UtcNullableDateTimeJsonConverter());
        });
}
