using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Constants;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>AI Content Studio phase 1: the charge gate and the tenant's activity record, the approval rule, the
/// storage key policy and the studio settings.</summary>
public sealed class ContentStudioTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private sealed class RecordingAlerts : IQuotaAlertService
    {
        public List<QuotaType> Exhausted { get; } = new();

        public Task<bool> NotifyIfExhaustedAsync(Guid tenantId, QuotaType quotaType, CancellationToken cancellationToken = default)
        {
            Exhausted.Add(quotaType);
            return Task.FromResult(true);
        }

        public Task<int> EvaluateAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class FakeClock : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; }
        public DateTime IstNow => UtcNow.AddHours(5.5);
    }

    private sealed class FakeTenantContext : ITenantContext
    {
        public Guid? TenantId => null;
        public bool IsPlatformSuperAdmin => true;
        public void SetTenant(Guid tenantId) { }
    }

    private sealed class FakeCurrentUser : ICurrentUserService
    {
        public Guid? UserId => null;
        public string? Email => null;
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public Guid? TenantId => null;
        public Guid? ImpersonatorUserId => null;
    }

    /// <summary>The tenant's effective studio settings - what its override over the platform default resolves to.</summary>
    private sealed class FakeStudioConfig
    {
        public StudioOptions Value { get; set; } = new() { Enabled = true };

        public ITenantConfigOverrideProvider Provider => Fake.Of<ITenantConfigOverrideProvider>((m, _) =>
            m.Name == nameof(ITenantConfigOverrideProvider.GetStudioOptionsForTenantAsync)
                ? Task.FromResult(Value)
                : throw new NotImplementedException(m.Name));
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeClock _clock = new() { UtcNow = Now };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly RecordingAlerts _alerts = new();
    private readonly FakeStudioConfig _options = new();
    private readonly StudioMeteringService _metering;
    private readonly StudioSettingsService _settings;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();

    public ContentStudioTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();
        _ledger = new QuotaLedgerService(_db, _clock);
        _metering = new StudioMeteringService(_db, _ledger, _alerts, _clock, _options.Provider);
        _settings = new StudioSettingsService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task GrantAsync(Guid tenant, QuotaType type, decimal units) =>
        _ledger.GrantCreditsAsync(tenant, new CreditPack { QuotaType = QuotaType.Credits, Name = "p", Units = units, PriceCents = 100 }, Guid.NewGuid(), Now);

    private async Task<decimal> BalanceAsync(Guid tenant, QuotaType type) =>
        (await _ledger.GetBalancesAsync(tenant)).Single(b => b.QuotaType == QuotaType.Credits).Balance; // one wallet: the type only says what the credits were for

    private static StudioChargeRequest Charge(Guid tenant, string key, StudioActivityKind kind = StudioActivityKind.Script, decimal? units = null) =>
        new(tenant, kind, key, $"Test {kind}", units, "ContentProject", "p1", "OpenAI");

    [Fact]
    public async Task The_balance_is_the_one_credit_wallet()
    {
        var types = (await _ledger.GetBalancesAsync(_tenant)).Select(b => b.QuotaType).ToList();

        Assert.Equal(new[] { QuotaType.Credits }, types);
    }

    [Fact]
    public async Task Charge_spends_units_and_records_what_it_was_for()
    {
        await GrantAsync(_tenant, QuotaType.ContentGenerations, 10);

        var result = await _metering.ChargeAsync(Charge(_tenant, "gen-1"));

        Assert.True(result.Allowed);
        Assert.Equal(9m, await BalanceAsync(_tenant, QuotaType.ContentGenerations));
        var row = await _db.StudioActivities.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(StudioActivityStatus.Charged, row.Status);
        Assert.Equal(QuotaType.ContentGenerations, row.QuotaType);
        Assert.Equal(1m, row.Units);
        Assert.Equal(9m, row.BalanceAfter);
        Assert.Equal("OpenAI", row.Provider);
        Assert.Equal("ContentProject", row.ReferenceType);
    }

    [Fact]
    public async Task A_video_render_and_a_generation_draw_from_the_same_wallet_and_are_told_apart_by_their_kind()
    {
        await GrantAsync(_tenant, QuotaType.Credits, 13);

        await _metering.ChargeAsync(Charge(_tenant, "render-1", StudioActivityKind.VideoRender));
        await _metering.ChargeAsync(Charge(_tenant, "gen-1"));

        Assert.Equal(11m, await BalanceAsync(_tenant, QuotaType.Credits));
        var kinds = (await _db.StudioActivities.IgnoreQueryFilters().ToListAsync()).ToDictionary(a => a.OperationKey, a => a.QuotaType);
        Assert.Equal(QuotaType.VideoRenders, kinds[StudioMeteringService.OperationKeyFor("render-1")]);
        Assert.Equal(QuotaType.ContentGenerations, kinds[StudioMeteringService.OperationKeyFor("gen-1")]);
    }

    [Fact]
    public async Task Retrying_the_same_operation_never_charges_twice()
    {
        await GrantAsync(_tenant, QuotaType.ContentGenerations, 10);

        var first = await _metering.ChargeAsync(Charge(_tenant, "gen-1"));
        var again = await _metering.ChargeAsync(Charge(_tenant, "gen-1"));

        Assert.True(again.AlreadyCharged);
        Assert.Equal(first.ActivityId, again.ActivityId);
        Assert.Equal(9m, await BalanceAsync(_tenant, QuotaType.ContentGenerations));
        Assert.Equal(1, await _db.StudioActivities.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Charge_with_no_quota_is_blocked_spends_nothing_and_is_still_recorded()
    {
        var result = await _metering.ChargeAsync(Charge(_tenant, "gen-1"));

        Assert.False(result.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        var row = await _db.StudioActivities.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(StudioActivityStatus.Blocked, row.Status);
        Assert.Equal(0m, row.Units);
        Assert.Contains(QuotaType.ContentGenerations, _alerts.Exhausted);
    }

    [Fact]
    public async Task Refund_returns_the_units_and_a_second_refund_does_nothing()
    {
        await GrantAsync(_tenant, QuotaType.VideoRenders, 3);
        var charged = await _metering.ChargeAsync(Charge(_tenant, "render-1", StudioActivityKind.VideoRender));

        await _metering.RefundAsync(charged.ActivityId, "Render provider failed");
        await _metering.RefundAsync(charged.ActivityId, "Render provider failed");

        Assert.Equal(3m, await BalanceAsync(_tenant, QuotaType.VideoRenders));
        var row = await _db.StudioActivities.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(StudioActivityStatus.Refunded, row.Status);
        Assert.Equal(1m, row.UnitsRefunded);
        Assert.Equal("Render provider failed", row.Detail);
        Assert.Equal(3m, row.BalanceAfter);
    }

    [Fact]
    public async Task Complete_keeps_the_units_spent_and_cannot_be_refunded_into_a_second_state()
    {
        await GrantAsync(_tenant, QuotaType.ContentGenerations, 5);
        var charged = await _metering.ChargeAsync(Charge(_tenant, "gen-1"));

        await _metering.CompleteAsync(charged.ActivityId);

        var row = await _db.StudioActivities.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(StudioActivityStatus.Completed, row.Status);
        Assert.NotNull(row.CompletedAtUtc);
        Assert.Equal(4m, await BalanceAsync(_tenant, QuotaType.ContentGenerations));
    }

    [Fact]
    public async Task A_free_step_is_recorded_without_touching_the_balance()
    {
        await GrantAsync(_tenant, QuotaType.ContentGenerations, 5);

        var result = await _metering.ChargeAsync(Charge(_tenant, "stock-1", StudioActivityKind.StockAssetSearch));

        Assert.True(result.Allowed);
        Assert.Equal(5m, await BalanceAsync(_tenant, QuotaType.ContentGenerations));
        var row = await _db.StudioActivities.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(0m, row.Units);
        Assert.Equal(5m, row.BalanceAfter);
    }

    [Fact]
    public async Task Activity_is_only_ever_the_callers_own_and_filters_apply()
    {
        await GrantAsync(_tenant, QuotaType.ContentGenerations, 10);
        await GrantAsync(_tenant, QuotaType.VideoRenders, 10);
        await GrantAsync(_other, QuotaType.ContentGenerations, 10);
        await _metering.ChargeAsync(Charge(_tenant, "a"));
        await _metering.ChargeAsync(Charge(_tenant, "b", StudioActivityKind.VideoRender));
        await _metering.ChargeAsync(Charge(_other, "a"));

        var mine = await _metering.GetActivityAsync(_tenant, new StudioActivityQuery());
        var renders = await _metering.GetActivityAsync(_tenant, new StudioActivityQuery { Kind = StudioActivityKind.VideoRender });
        var theirs = await _metering.GetActivityAsync(_other, new StudioActivityQuery());

        Assert.Equal(2, mine.TotalCount);
        Assert.All(mine.Items, i => Assert.StartsWith("studio:", i.OperationKey));
        Assert.Single(renders.Items);
        Assert.Equal(1, theirs.TotalCount);
    }

    [Fact]
    public async Task Overview_totals_charged_refunded_and_blocked_for_the_last_30_days()
    {
        await GrantAsync(_tenant, QuotaType.ContentGenerations, 3);
        var a = await _metering.ChargeAsync(Charge(_tenant, "a"));
        await _metering.ChargeAsync(Charge(_tenant, "b"));
        await _metering.RefundAsync(a.ActivityId, "failed");
        await _metering.ChargeAsync(Charge(_tenant, "c", units: 5m)); // more than is left: blocked

        var overview = await _metering.GetOverviewAsync(_tenant);

        var gen = overview.Last30Days.Single(t => t.QuotaType == QuotaType.ContentGenerations);
        Assert.Equal(2m, gen.Charged);
        Assert.Equal(1m, gen.Refunded);
        Assert.Equal(1m, gen.Net);
        Assert.Equal(1, gen.Blocked);
        Assert.True(overview.Enabled);
        Assert.Equal(QuotaType.Credits, Assert.Single(overview.Balances).QuotaType);
    }

    [Fact]
    public async Task Overview_reports_the_module_as_off_when_the_platform_switch_is_off()
    {
        _options.Value = new StudioOptions { Enabled = false };

        Assert.False((await _metering.GetOverviewAsync(_tenant)).Enabled);
    }

    [Fact]
    public async Task Settings_default_to_admin_only_and_can_be_widened_per_tenant()
    {
        Assert.Equal(StudioApprovalPolicy.AdminOnly, (await _settings.GetAsync(_tenant)).ApprovalPolicy);

        await _settings.UpdateAsync(_tenant, new UpdateStudioSettingsRequest(StudioApprovalPolicy.AdminAndSalesManager));

        Assert.Equal(StudioApprovalPolicy.AdminAndSalesManager, (await _settings.GetAsync(_tenant)).ApprovalPolicy);
        Assert.Equal(StudioApprovalPolicy.AdminOnly, (await _settings.GetAsync(_other)).ApprovalPolicy);
        Assert.Equal(1, await _db.TenantStudioSettings.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Settings_reject_an_unknown_policy()
    {
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            _settings.UpdateAsync(_tenant, new UpdateStudioSettingsRequest((StudioApprovalPolicy)99)));
    }

    [Theory]
    [InlineData(StudioApprovalPolicy.AdminOnly, AppRoles.Admin, true)]
    [InlineData(StudioApprovalPolicy.AdminOnly, AppRoles.SalesManager, false)]
    [InlineData(StudioApprovalPolicy.AdminOnly, AppRoles.SalesAgent, false)]
    [InlineData(StudioApprovalPolicy.AdminAndSalesManager, AppRoles.Admin, true)]
    [InlineData(StudioApprovalPolicy.AdminAndSalesManager, AppRoles.SalesManager, true)]
    [InlineData(StudioApprovalPolicy.AdminAndSalesManager, AppRoles.SalesAgent, false)]
    [InlineData(StudioApprovalPolicy.AdminAndSalesManager, AppRoles.PlatformSuperAdmin, false)]
    public void Approval_follows_the_tenants_policy(StudioApprovalPolicy policy, string role, bool expected) =>
        Assert.Equal(expected, StudioApprovalRules.CanApprove(policy, new[] { role }));

    [Fact]
    public void Storage_keys_are_unguessable_tenant_scoped_and_safe()
    {
        var a = StudioStorageKeys.NewKey("studio", _tenant, StudioStorageZone.Uploads, ".MP4", Now);
        var b = StudioStorageKeys.NewKey("studio/", _tenant, StudioStorageZone.Uploads, "mp4", Now);

        Assert.NotEqual(a, b);
        Assert.StartsWith($"studio/{_tenant:N}/uploads/202610/", a);
        Assert.EndsWith(".mp4", a);
        Assert.True(StudioStorageKeys.BelongsToTenant("studio", _tenant, a));
        Assert.False(StudioStorageKeys.BelongsToTenant("studio", _other, a));
        Assert.False(StudioStorageKeys.BelongsToTenant("studio", _tenant, $"studio/{_tenant:N}/../{_other:N}/x.mp4"));
        var hostile = StudioStorageKeys.NewKey("studio", _tenant, StudioStorageZone.Renders, "../../etc", Now);
        Assert.DoesNotContain("..", hostile);
        Assert.True(StudioStorageKeys.BelongsToTenant("studio", _tenant, hostile));
    }

    [Fact]
    public void Storage_mode_defaults_to_the_public_bucket_and_reads_the_setting()
    {
        Assert.Equal(StudioStorageMode.PublicBucket, new StudioOptions().ResolvedStorageMode);
        Assert.Equal(StudioStorageMode.SignedUrls, new StudioOptions { StorageMode = "signedurls" }.ResolvedStorageMode);
        Assert.Equal(StudioStorageMode.PublicBucket, new StudioOptions { StorageMode = "nonsense" }.ResolvedStorageMode);
    }
}
