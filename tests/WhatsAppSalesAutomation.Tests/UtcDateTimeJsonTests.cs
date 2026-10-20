using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Api.Serialization;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The API writes every UTC timestamp with a trailing Z. EF Core hands back datetimes with Kind Unspecified, which serialise with no zone and
/// which a browser then shows as local time - the bug this guards.</summary>
public sealed class UtcDateTimeJsonTests
{
    private sealed record Dto(DateTime At, DateTime? Maybe, DateTime? None);

    private static JsonSerializerOptions ControllerOptions()
    {
        var services = new ServiceCollection();
        services.AddControllers().AddUtcDateTimeJson();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
    }

    [Fact]
    public void An_unspecified_kind_datetime_as_read_from_the_database_is_written_as_utc_with_a_trailing_z()
    {
        var fromDatabase = new DateTime(2026, 10, 20, 7, 30, 15, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(new Dto(fromDatabase, fromDatabase, null), ControllerOptions());

        Assert.Contains("\"at\":\"2026-10-20T07:30:15Z\"", json);
        Assert.Contains("\"maybe\":\"2026-10-20T07:30:15Z\"", json);
        Assert.Contains("\"none\":null", json);
    }

    [Fact]
    public void Utc_and_local_values_are_written_as_the_same_instant_in_utc()
    {
        var utc = new DateTime(2026, 10, 20, 7, 30, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();

        var options = ControllerOptions();
        Assert.Equal(JsonSerializer.Serialize(new Dto(utc, null, null), options), JsonSerializer.Serialize(new Dto(local, null, null), options));
        Assert.EndsWith("Z", JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new Dto(utc, null, null), options)).GetProperty("at").GetString());
    }

    [Fact]
    public void Fractions_of_a_second_are_kept_and_a_value_round_trips_as_utc()
    {
        var value = new DateTime(2026, 10, 20, 7, 30, 15, 123, DateTimeKind.Utc);
        var options = ControllerOptions();

        var json = JsonSerializer.Serialize(new Dto(value, null, null), options);
        var back = JsonSerializer.Deserialize<Dto>(json, options)!;

        Assert.Contains("2026-10-20T07:30:15.123Z", json);
        Assert.Equal(value, back.At);
        Assert.Equal(DateTimeKind.Utc, back.At.Kind);
    }

    [Theory]
    [InlineData("2026-10-20T07:30:00Z", 7)]
    [InlineData("2026-10-20T07:30:00", 7)] // no zone: taken as UTC
    [InlineData("2026-10-20T13:00:00+05:30", 7)] // an offset is converted to UTC
    public void Reading_accepts_a_zone_or_none_and_always_yields_utc(string text, int hour)
    {
        var back = JsonSerializer.Deserialize<Dto>($"{{\"at\":\"{text}\"}}", ControllerOptions())!;

        Assert.Equal(DateTimeKind.Utc, back.At.Kind);
        Assert.Equal(hour, back.At.Hour);
    }

    [Fact]
    public void An_unreadable_date_is_an_error_not_a_silent_default()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Dto>("{\"at\":\"not a date\"}", ControllerOptions()));
    }
}
