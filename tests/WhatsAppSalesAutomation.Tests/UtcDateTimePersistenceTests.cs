using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Api.Serialization;
using WhatsAppSalesAutomation.Application.Campaigns;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>
/// A timestamp read back from the database is an instant in UTC and reaches the browser with a trailing Z, from a controller, a
/// minimal-API endpoint or a SignalR push alike. The two wall-clock columns (a tenant's own local time) are the exception: they come
/// back Unspecified and are written with no zone, so the browser does not shift them by the viewer's offset.
/// </summary>
public sealed class UtcDateTimePersistenceTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000b");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;

    public UtcDateTimePersistenceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
        _db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static JsonSerializerOptions ControllerOptions()
    {
        var services = new ServiceCollection();
        services.AddControllers().AddUtcDateTimeJson();
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
    }

    private async Task<Campaign> SaveAndReloadAsync(Campaign campaign)
    {
        _db.Campaigns.Add(campaign);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return await _db.Campaigns.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == campaign.Id);
    }

    private static Campaign NewCampaign() => new()
    {
        TenantId = Tenant,
        Name = "Diwali offer",
        CreatedAt = new DateTime(2026, 10, 20, 7, 30, 15, DateTimeKind.Utc),
        StartedAt = new DateTime(2026, 10, 21, 4, 0, 0, DateTimeKind.Utc),
        StoppedAt = null,
        // 10:00 on the tenant's own clock - the Kind it arrives with must not turn it into an instant.
        ScheduledStartAt = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task A_timestamp_read_back_from_the_database_is_utc_for_both_datetime_and_nullable_datetime()
    {
        var loaded = await SaveAndReloadAsync(NewCampaign());

        Assert.Equal(DateTimeKind.Utc, loaded.CreatedAt.Kind);
        Assert.Equal(new DateTime(2026, 10, 20, 7, 30, 15, DateTimeKind.Utc), loaded.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, loaded.StartedAt!.Value.Kind);
        Assert.Null(loaded.StoppedAt);
    }

    [Fact]
    public async Task A_projected_timestamp_is_utc_too()
    {
        var saved = await SaveAndReloadAsync(NewCampaign());

        var projected = await _db.Campaigns.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == saved.Id).Select(c => new { c.CreatedAt, c.StartedAt }).SingleAsync();

        Assert.Equal(DateTimeKind.Utc, projected.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, projected.StartedAt!.Value.Kind);
    }

    [Fact]
    public async Task A_wall_clock_column_comes_back_unspecified_with_its_digits_unchanged()
    {
        var loaded = await SaveAndReloadAsync(NewCampaign());

        Assert.Equal(DateTimeKind.Unspecified, loaded.ScheduledStartAt!.Value.Kind);
        Assert.Equal(new DateTime(2026, 11, 25, 10, 0, 0), loaded.ScheduledStartAt.Value);
    }

    [Fact]
    public async Task A_serialized_entity_date_ends_with_z_and_the_wall_clock_one_does_not()
    {
        var loaded = await SaveAndReloadAsync(NewCampaign());

        var json = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(loaded.ToDto(0, new Dictionary<Guid, string>()), ControllerOptions()));

        Assert.Equal("2026-10-20T07:30:15Z", json.GetProperty("createdAt").GetString());
        Assert.Equal("2026-10-21T04:00:00Z", json.GetProperty("startedAt").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("stoppedAt").ValueKind);
        Assert.Equal("2026-11-25T10:00:00", json.GetProperty("scheduledStartAt").GetString());
    }

    [Fact]
    public void Every_datetime_column_is_read_as_utc_except_the_two_wall_clock_ones()
    {
        var wallClock = new HashSet<string> { "Campaign.ScheduledStartAt", "AutoCampaignEnrollment.ExecutionDateLocal" };

        var properties = _db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => (Name: $"{e.ClrType.Name}.{p.Name}", Property: p)))
            .Where(x => x.Property.ClrType == typeof(DateTime) || x.Property.ClrType == typeof(DateTime?))
            .ToList();

        Assert.NotEmpty(properties);
        foreach (var (name, property) in properties)
        {
            var expected = wallClock.Contains(name) ? typeof(WallClockDateTimeConverter) : typeof(UtcDateTimeConverter);
            Assert.True(property.GetValueConverter()?.GetType() == expected, $"{name} should use {expected.Name}, has {property.GetValueConverter()?.GetType().Name ?? "none"}");
        }
    }

    [Theory]
    [InlineData("2026-11-25T10:00:00")]
    [InlineData("2026-11-25T10:00:00Z")]
    [InlineData("2026-11-25T10:00:00+05:30")]
    public void A_scheduled_start_in_a_request_keeps_its_digits_whatever_zone_is_attached(string text)
    {
        var request = JsonSerializer.Deserialize<CreateCampaignRequest>($"{{\"name\":\"x\",\"scheduledStartAt\":\"{text}\"}}", ControllerOptions())!;

        Assert.Equal(new DateTime(2026, 11, 25, 10, 0, 0), request.ScheduledStartAt);
        Assert.Equal(DateTimeKind.Unspecified, request.ScheduledStartAt!.Value.Kind);
    }

    [Fact]
    public void A_null_scheduled_start_round_trips_as_null()
    {
        var options = ControllerOptions();
        var request = JsonSerializer.Deserialize<UpdateCampaignRequest>("{\"name\":\"x\",\"scheduledStartAt\":null}", options)!;

        Assert.Null(request.ScheduledStartAt);
        Assert.Contains("\"scheduledStartAt\":null", JsonSerializer.Serialize(request, options));
    }

    [Fact]
    public void An_enrollment_execution_date_is_written_without_a_zone()
    {
        var dto = new AutoCampaignEnrollmentDto(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), null, null, null, null,
            new DateTime(2026, 11, 23), "Enrolled", null, new DateTime(2026, 11, 23, 5, 0, 0));

        var json = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(dto, ControllerOptions()));

        Assert.Equal("2026-11-23T00:00:00", json.GetProperty("executionDateLocal").GetString());
        Assert.Equal("2026-11-23T05:00:00Z", json.GetProperty("createdAt").GetString());
    }

    [Fact]
    public void Minimal_api_endpoints_and_signalr_pushes_also_write_utc_with_a_trailing_z()
    {
        var services = new ServiceCollection();
        services.AddSignalR();
        services.AddUtcDateTimeJsonForEndpointsAndHubs();
        using var provider = services.BuildServiceProvider();

        var fromDatabase = new { createdAt = new DateTime(2026, 10, 20, 7, 30, 15, DateTimeKind.Unspecified), maybe = (DateTime?)null };
        var hub = provider.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value.PayloadSerializerOptions;
        var http = provider.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;

        Assert.Contains("\"createdAt\":\"2026-10-20T07:30:15Z\"", JsonSerializer.Serialize(fromDatabase, hub));
        Assert.Contains("\"createdAt\":\"2026-10-20T07:30:15Z\"", JsonSerializer.Serialize(fromDatabase, http));
    }
}
