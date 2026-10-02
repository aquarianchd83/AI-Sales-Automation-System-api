using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Domain.Entities.Ai;
using WhatsAppSalesAutomation.Domain.Entities.Audit;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Identity;
using WhatsAppSalesAutomation.Domain.Entities.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Webhooks;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Maintenance;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The daily clean-up: what has outlived its retention period goes, everything else - above all anything still unprocessed or
/// unread - stays, and a second pass finds nothing left to do.</summary>
public sealed class DataRetentionTests : IDisposable
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly TestClock _clock = new();
    private readonly RetentionOptions _options = new();

    public DataRetentionTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private DateTime Ago(int days) => _clock.UtcNow.AddDays(-days);

    private Task<DataRetentionResultView> Run() => RunAsync();

    private async Task<DataRetentionResultView> RunAsync()
    {
        var service = new DataRetentionService(_db, new FixedOptions<RetentionOptions>(_options), _clock, NullLogger<DataRetentionService>.Instance);
        var result = await service.RunAsync();
        _db.ChangeTracker.Clear();
        return new DataRetentionResultView(result.Deleted, result.Total);
    }

    private sealed record DataRetentionResultView(IReadOnlyDictionary<string, int> Deleted, int Total);

    private WebhookEvent Webhook(int daysOld, WebhookProcessingStatus status) => new()
    {
        TenantId = Tenant, EventType = "message", RawPayload = "{}", ReceivedAt = Ago(daysOld), ProcessingStatus = status
    };

    [Fact]
    public async Task Processed_and_failed_webhook_payloads_expire_but_an_unprocessed_one_never_does()
    {
        var oldProcessed = Webhook(100, WebhookProcessingStatus.Processed);
        var oldFailed = Webhook(100, WebhookProcessingStatus.Failed);
        var oldPending = Webhook(100, WebhookProcessingStatus.Pending);
        var recent = Webhook(10, WebhookProcessingStatus.Processed);
        _db.WebhookEvents.AddRange(oldProcessed, oldFailed, oldPending, recent);
        await _db.SaveChangesAsync();

        var result = await Run();

        Assert.Equal(2, result.Deleted["WebhookEvents"]);
        var left = await _db.WebhookEvents.IgnoreQueryFilters().Select(w => w.Id).ToListAsync();
        Assert.Equal(new[] { oldPending.Id, recent.Id }.OrderBy(i => i), left.OrderBy(i => i));
    }

    [Fact]
    public async Task Dead_refresh_tokens_go_and_live_ones_stay()
    {
        var user = Guid.NewGuid();
        var expiredLongAgo = new RefreshToken { UserId = user, TokenHash = "a", ExpiresAt = Ago(40) };
        var revokedLongAgo = new RefreshToken { UserId = user, TokenHash = "b", ExpiresAt = Ago(35), RevokedAt = Ago(40) };
        var expiredRecently = new RefreshToken { UserId = user, TokenHash = "c", ExpiresAt = Ago(10) };
        var live = new RefreshToken { UserId = user, TokenHash = "d", ExpiresAt = _clock.UtcNow.AddDays(5) };
        _db.RefreshTokens.AddRange(expiredLongAgo, revokedLongAgo, expiredRecently, live);
        await _db.SaveChangesAsync();

        var result = await Run();

        Assert.Equal(2, result.Deleted["RefreshTokens"]);
        Assert.Equal(new[] { "c", "d" }, (await _db.RefreshTokens.IgnoreQueryFilters().Select(t => t.TokenHash).ToListAsync()).OrderBy(h => h));
    }

    [Fact]
    public async Task Only_notifications_that_were_read_expire()
    {
        _db.TenantNotifications.AddRange(
            new TenantNotification { TenantId = Tenant, EpisodeKey = "read-old", Title = "t", Body = "b", CreatedAt = Ago(200), AcknowledgedAtUtc = Ago(199) },
            new TenantNotification { TenantId = Tenant, EpisodeKey = "unread-old", Title = "t", Body = "b", CreatedAt = Ago(200) },
            new TenantNotification { TenantId = Tenant, EpisodeKey = "read-new", Title = "t", Body = "b", CreatedAt = Ago(20), AcknowledgedAtUtc = Ago(19) });
        _db.PlatformNotifications.AddRange(
            new PlatformNotification { EpisodeKey = "p-read-old", Title = "t", Body = "b", CreatedAt = Ago(200), AcknowledgedAtUtc = Ago(199) },
            new PlatformNotification { EpisodeKey = "p-unread-old", Title = "t", Body = "b", CreatedAt = Ago(200) });
        await _db.SaveChangesAsync();

        var result = await Run();

        Assert.Equal(1, result.Deleted["TenantNotifications"]);
        Assert.Equal(1, result.Deleted["PlatformNotifications"]);
        Assert.Equal(new[] { "read-new", "unread-old" }, (await _db.TenantNotifications.IgnoreQueryFilters().Select(n => n.EpisodeKey).ToListAsync()).OrderBy(k => k));
        Assert.Equal("p-unread-old", await _db.PlatformNotifications.IgnoreQueryFilters().Select(n => n.EpisodeKey).SingleAsync());
    }

    [Fact]
    public async Task Old_ai_turns_and_old_audit_rows_expire()
    {
        _db.AiInteractions.AddRange(
            new AiInteraction { TenantId = Tenant, ConversationId = Guid.NewGuid(), InboundMessageId = Guid.NewGuid(), CreatedAt = Ago(400) },
            new AiInteraction { TenantId = Tenant, ConversationId = Guid.NewGuid(), InboundMessageId = Guid.NewGuid(), CreatedAt = Ago(10) });
        _db.AuditLogs.AddRange(
            new AuditLog { TenantId = Tenant, EntityName = "Lead", PerformedAt = Ago(800), CreatedAt = Ago(800) },
            new AuditLog { TenantId = Tenant, EntityName = "Lead", PerformedAt = Ago(10), CreatedAt = Ago(10) });
        await _db.SaveChangesAsync();

        var result = await Run();

        Assert.Equal(1, result.Deleted["AiInteractions"]);
        Assert.Equal(1, result.Deleted["AuditLogs"]);
        Assert.Equal(1, await _db.AiInteractions.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await _db.AuditLogs.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_second_pass_finds_nothing_left_to_do()
    {
        _db.WebhookEvents.Add(Webhook(100, WebhookProcessingStatus.Processed));
        await _db.SaveChangesAsync();

        Assert.Equal(1, (await Run()).Total);
        Assert.Equal(0, (await Run()).Total);
    }

    [Fact]
    public async Task Zero_days_keeps_that_kind_of_data_forever_and_is_not_even_reported()
    {
        _options.WebhookEventDays = 0;
        _db.WebhookEvents.Add(Webhook(1000, WebhookProcessingStatus.Processed));
        await _db.SaveChangesAsync();

        var result = await Run();

        Assert.False(result.Deleted.ContainsKey("WebhookEvents"));
        Assert.Equal(1, await _db.WebhookEvents.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_tiny_setting_counts_as_a_week_so_a_typo_cannot_wipe_recent_records()
    {
        _options.AuditLogDays = 1;
        _db.AuditLogs.AddRange(
            new AuditLog { TenantId = Tenant, EntityName = "Lead", PerformedAt = Ago(5), CreatedAt = Ago(5) },
            new AuditLog { TenantId = Tenant, EntityName = "Lead", PerformedAt = Ago(10), CreatedAt = Ago(10) });
        await _db.SaveChangesAsync();

        await Run();

        Assert.Equal(Ago(5), await _db.AuditLogs.IgnoreQueryFilters().Select(a => a.PerformedAt).SingleAsync());
    }

    [Fact]
    public async Task A_big_backlog_is_cleared_in_batches()
    {
        _options.BatchSize = 100;
        for (var i = 0; i < 250; i++)
            _db.WebhookEvents.Add(Webhook(100, WebhookProcessingStatus.Processed));
        await _db.SaveChangesAsync();

        var result = await Run();

        Assert.Equal(250, result.Deleted["WebhookEvents"]);
        Assert.Equal(0, await _db.WebhookEvents.IgnoreQueryFilters().CountAsync());
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(3, 7)]
    [InlineData(7, 7)]
    [InlineData(90, 90)]
    public void The_effective_period_is_off_for_zero_and_never_shorter_than_a_week(int configured, int? expected)
    {
        Assert.Equal(expected, RetentionOptions.Effective(configured));
    }

    [Fact]
    public void The_retention_settings_are_in_the_settings_catalog()
    {
        foreach (var key in new[] { "WebhookEventDays", "RefreshTokenDays", "NotificationDays", "AiInteractionDays", "AuditLogDays", "BatchSize" })
            Assert.Contains(WhatsAppSalesAutomation.Application.Settings.AppSettingCatalog.All, d => d.Key == "Retention:" + key);
    }
}
