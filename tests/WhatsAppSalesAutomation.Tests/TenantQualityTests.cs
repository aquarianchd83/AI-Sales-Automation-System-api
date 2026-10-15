using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Compliance;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What a quality change does to a tenant, and how that changes what it may send.</summary>
public sealed class TenantQualityTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme", CreatedAt = Now.AddDays(-1) };
    private readonly List<TenantNotificationRequest> _notices = new();
    private readonly List<(string Action, string? Details)> _audit = new();
    private readonly Clock _clock = new() { UtcNow = Now };
    private readonly TenantQualityService _service;

    public TenantQualityTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new Ambient(_tenant.Id), new NoUser()) { StampTenantId = _tenant.Id };
        _db.Database.EnsureCreated();
        _db.Tenants.Add(_tenant);
        _db.SaveChanges();

        var notifier = Stub<ITenantNotifier>.Create(new()
        {
            ["NotifyAsync"] = args => { _notices.Add((TenantNotificationRequest)args![0]!); return Task.FromResult(true); }
        });
        var audit = Stub<IPlatformAuditService>.Create(new()
        {
            ["LogAsync"] = args => { _audit.Add(((string)args![2]!, (string?)args[5])); return Task.CompletedTask; }
        });

        _service = new TenantQualityService(_db, new Ambient(_tenant.Id), notifier, audit, _clock, NullLogger<TenantQualityService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Tenant Reload() => _db.Tenants.AsNoTracking().Single();

    [Fact]
    public async Task Yellow_warns_the_tenant_and_halves_the_cap_without_pausing()
    {
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "FLAGGED", null);

        var tenant = Reload();
        Assert.Equal(WhatsAppQualityRating.Yellow, tenant.QualityRating);
        Assert.Null(tenant.SendingPausedAt);
        Assert.Equal(TenantStatus.Trial, tenant.Status);
        Assert.Equal(TenantNotificationKind.QualityWarning, Assert.Single(_notices).Kind);
        Assert.Contains(_audit, a => a.Action == PlatformAuditActions.TenantQualityWarning);

        Assert.Equal(100, SendLimits.EffectiveDailyCap(new MessagingOptions(), tenant, Now));
    }

    [Fact]
    public async Task Red_pauses_sending_and_the_pause_blocks_every_send()
    {
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "DOWNGRADE", "RED");

        var tenant = Reload();
        Assert.Equal(WhatsAppQualityRating.Red, tenant.QualityRating);
        Assert.NotNull(tenant.SendingPausedAt);
        Assert.Equal(TenantNotificationKind.SendingPaused, Assert.Single(_notices).Kind);
        Assert.Contains(_audit, a => a.Action == PlatformAuditActions.TenantSendingPaused);

        var decision = await SendLimits.CheckAsync(_db, _tenant.Id, Guid.NewGuid(), TemplateCategory.Utility, new MessagingOptions(), Now, default);
        Assert.False(decision.Allowed);
        Assert.Contains("paused", decision.Reason);
    }

    [Fact]
    public async Task A_recovered_rating_does_not_reopen_sending_but_a_person_can()
    {
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "DOWNGRADE", "RED");
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "UNFLAGGED", null);

        Assert.Equal(WhatsAppQualityRating.Green, Reload().QualityRating);
        Assert.NotNull(Reload().SendingPausedAt);

        await _service.ResumeSendingAsync(_tenant.Id, Guid.NewGuid(), "ops@example.com");

        Assert.Null(Reload().SendingPausedAt);
        Assert.Contains(_audit, a => a.Action == PlatformAuditActions.TenantSendingResumed);
    }

    [Fact]
    public async Task A_second_drop_after_recovering_within_the_window_suspends_the_tenant()
    {
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "FLAGGED", null);
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "UNFLAGGED", null);
        Assert.Equal(TenantStatus.Trial, Reload().Status);

        _clock.UtcNow = Now.AddDays(10);
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "FLAGGED", null);

        Assert.Equal(TenantStatus.Suspended, Reload().Status);
        Assert.Contains(_audit, a => a.Action == PlatformAuditActions.TenantSuspended);
    }

    [Fact]
    public async Task A_second_drop_long_after_the_first_is_only_a_new_warning()
    {
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "FLAGGED", null);
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "UNFLAGGED", null);

        _clock.UtcNow = Now.AddDays(TenantQualityService.RepeatViolationWindowDays + 5);
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "FLAGGED", null);

        Assert.NotEqual(TenantStatus.Suspended, Reload().Status);
    }

    [Fact]
    public async Task Yellow_turning_red_is_one_incident_getting_worse_not_a_repeat()
    {
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "FLAGGED", null);
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "DOWNGRADE", "RED");

        var tenant = Reload();
        Assert.Equal(TenantStatus.Trial, tenant.Status);
        Assert.NotNull(tenant.SendingPausedAt);
    }

    [Fact]
    public async Task Redelivered_events_and_tier_only_events_change_nothing()
    {
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "FLAGGED", null);
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "FLAGGED", null);
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "UPGRADE", null);
        await _service.ApplyPhoneQualityEventAsync(_tenant.Id, "ONBOARDING", null);

        Assert.Single(_notices);
        Assert.Equal(WhatsAppQualityRating.Yellow, Reload().QualityRating);
    }

    [Fact]
    public void Green_earns_higher_caps_every_week_and_a_week_of_yellow_loses_them()
    {
        var options = new MessagingOptions { DailyTemplateCap = 200, CapStepUpAfterGreenDays = 7, CapStepUps = new[] { 500, 1000 } };
        Tenant Green(double days) => new() { QualityRating = WhatsAppQualityRating.Green, QualityRatingSince = Now.AddDays(-days) };

        Assert.Equal(200, SendLimits.EffectiveDailyCap(options, Green(6), Now));
        Assert.Equal(500, SendLimits.EffectiveDailyCap(options, Green(7), Now));
        Assert.Equal(500, SendLimits.EffectiveDailyCap(options, Green(13), Now));
        Assert.Equal(1000, SendLimits.EffectiveDailyCap(options, Green(14), Now));
        Assert.Equal(1000, SendLimits.EffectiveDailyCap(options, Green(90), Now));

        var yellow = new Tenant { QualityRating = WhatsAppQualityRating.Yellow, QualityRatingSince = Now.AddDays(-30) };
        Assert.Equal(100, SendLimits.EffectiveDailyCap(options, yellow, Now));
    }

    [Fact]
    public void The_webhook_parser_reads_a_quality_update_and_routes_it_by_waba_not_phone_number_id()
    {
        const string payload = """
            {"object":"whatsapp_business_account","entry":[{"id":"WABA-1","changes":[{"field":"phone_number_quality_update",
            "value":{"display_phone_number":"+91 98765 43210","event":"FLAGGED","current_limit":"TIER_1K"}}]}]}
            """;

        var result = new WhatsAppWebhookParser(NullLogger<WhatsAppWebhookParser>.Instance).Parse(payload);

        Assert.Null(result.PhoneNumberId);
        Assert.Equal("WABA-1", result.WabaId);
        var update = Assert.Single(result.QualityUpdates!);
        Assert.Equal("FLAGGED", update.Event);
        Assert.Equal("+91 98765 43210", update.DisplayPhoneNumber);
        Assert.Empty(result.Messages);
    }

    private sealed class Clock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; }
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class Ambient : ITenantContext
    {
        private Guid _tenantId;
        public Ambient(Guid tenantId) => _tenantId = tenantId;
        public Guid? TenantId => _tenantId;
        public bool IsPlatformSuperAdmin => false;
        public void SetTenant(Guid tenantId) => _tenantId = tenantId;
    }

    private sealed class NoUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }
}
