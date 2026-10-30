using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The ledger underneath the single credit wallet: grants, spends (each tagged with its feature), give-backs, expiry of purchased credits, and the
/// invariant that the ledger always sums to what is left in the grants.</summary>
public sealed class QuotaLedgerServiceTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeClock _clock = new() { UtcNow = Start.AddDays(11) };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _service;
    private readonly Guid _tenant = Guid.NewGuid();

    public QuotaLedgerServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();
        _service = new QuotaLedgerService(_db, _clock);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>1,000 plan credits, the way a plan puts them in the wallet.</summary>
    private Task<bool> GrantPlanAsync(decimal units = 1000m, string key = "monthly:202609") =>
        _service.GrantPlanCreditsAsync(_tenant, units, key, "Premium plan credits", QuotaGrantOrigin.PlanAllocation);

    private Task<ConsumeResult> SpendAsync(decimal units, string key, bool partial = false, PlatformFeature? feature = PlatformFeature.WhatsAppMessaging) =>
        _service.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.Credits, units, key, "Message", key, AllowPartial: partial, Feature: feature));

    private async Task<decimal> BalanceAsync() => (await _service.GetBalancesAsync(_tenant)).Single(b => b.QuotaType == QuotaType.Credits).Balance;

    private static CreditPack Pack(decimal credits = 1000m) => new() { QuotaType = QuotaType.Credits, Name = "pack", Units = credits, PriceCents = 2000 };

    // The invariant the whole design rests on: what the ledger sums to is exactly what is left in the grants.
    private async Task AssertLedgerMatchesGrantsAsync()
    {
        var ledger = (await _db.QuotaLedgerEntries.IgnoreQueryFilters().Where(e => e.TenantId == _tenant).ToListAsync()).Sum(e => e.UnitsDelta);
        var grants = (await _db.QuotaGrants.IgnoreQueryFilters().Where(g => g.TenantId == _tenant).ToListAsync()).Sum(g => g.UnitsRemaining);
        Assert.Equal(grants, ledger);
    }

    [Fact]
    public async Task There_is_one_wallet_and_it_starts_empty()
    {
        var balances = await _service.GetBalancesAsync(_tenant);

        var wallet = Assert.Single(balances);
        Assert.Equal((QuotaType.Credits, 0m), (wallet.QuotaType, wallet.Balance));
    }

    [Fact]
    public async Task Plan_credits_are_granted_once_per_operation_key()
    {
        Assert.True(await GrantPlanAsync());
        Assert.False(await GrantPlanAsync()); // the same month again: nothing happens
        Assert.True(await GrantPlanAsync(100m, "monthly:202610")); // the next month is a new grant

        Assert.Equal(1100m, await BalanceAsync());
        Assert.Equal(2, await _db.QuotaLedgerEntries.IgnoreQueryFilters().CountAsync());
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Plan_credits_never_expire()
    {
        await GrantPlanAsync();

        _clock.UtcNow = Start.AddYears(10);

        Assert.Equal(1000m, await BalanceAsync());
        Assert.Equal(0, await _service.ExpireDueAsync());
    }

    [Fact]
    public async Task Consumption_reduces_balance_and_records_running_balance_and_feature()
    {
        await GrantPlanAsync();
        var result = await SpendAsync(300, "m1");

        Assert.True(result.Sufficient);
        Assert.Equal(700m, result.BalanceAfter);
        Assert.Equal(700m, await BalanceAsync());
        var entry = await _db.QuotaLedgerEntries.IgnoreQueryFilters().SingleAsync(e => e.EntryType == QuotaEntryType.Consumption);
        Assert.Equal(PlatformFeature.WhatsAppMessaging, entry.Feature);
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Each_feature_has_its_own_ledger()
    {
        await GrantPlanAsync();
        await SpendAsync(300, "m1", feature: PlatformFeature.WhatsAppMessaging);
        await SpendAsync(50, "a1", feature: PlatformFeature.AiConversations);

        var messaging = await _service.GetLedgerAsync(_tenant, new QuotaLedgerQuery { Feature = PlatformFeature.WhatsAppMessaging, PageSize = 50 });
        var ai = await _service.GetLedgerAsync(_tenant, new QuotaLedgerQuery { Feature = PlatformFeature.AiConversations, PageSize = 50 });

        Assert.Equal(-300m, Assert.Single(messaging.Items).UnitsDelta);
        Assert.Equal(-50m, Assert.Single(ai.Items).UnitsDelta);
    }

    [Fact]
    public async Task The_ledger_can_be_limited_to_one_kind_of_entry()
    {
        await GrantPlanAsync();
        await SpendAsync(300, "m1");

        var spent = await _service.GetLedgerAsync(_tenant, new QuotaLedgerQuery { EntryType = QuotaEntryType.Consumption, PageSize = 50 });
        var bought = await _service.GetLedgerAsync(_tenant, new QuotaLedgerQuery { EntryType = QuotaEntryType.Purchase, PageSize = 50 });

        Assert.Equal(-300m, Assert.Single(spent.Items).UnitsDelta);
        Assert.Empty(bought.Items);
    }

    [Fact]
    public async Task The_summary_totals_each_kind_of_entry_each_feature_and_each_day()
    {
        await GrantPlanAsync();
        await SpendAsync(300, "m1", feature: PlatformFeature.WhatsAppMessaging);
        await SpendAsync(50, "a1", feature: PlatformFeature.AiConversations);

        var summary = await _service.GetLedgerSummaryAsync(_tenant, 7);

        Assert.Equal(-350m, summary.ByEntryType.Single(t => t.EntryType == QuotaEntryType.Consumption).Units);
        Assert.Equal(300m, summary.ByFeature.Single(f => f.Feature == PlatformFeature.WhatsAppMessaging).UnitsUsed);
        Assert.Equal(50m, summary.ByFeature.Single(f => f.Feature == PlatformFeature.AiConversations).UnitsUsed);
        Assert.Equal(7, summary.Daily.Count);
        Assert.Equal(350m, summary.Daily.Sum(d => d.Used));
    }

    [Fact]
    public async Task The_daily_series_can_be_narrowed_to_one_feature_without_changing_the_totals()
    {
        await GrantPlanAsync();
        await SpendAsync(300, "m1", feature: PlatformFeature.WhatsAppMessaging);
        await SpendAsync(50, "a1", feature: PlatformFeature.AiConversations);

        var summary = await _service.GetLedgerSummaryAsync(_tenant, 7, PlatformFeature.AiConversations);

        Assert.Equal(50m, summary.Daily.Sum(d => d.Used));
        Assert.Equal(0m, summary.Daily.Sum(d => d.Purchased));
        Assert.Equal(300m, summary.ByFeature.Single(f => f.Feature == PlatformFeature.WhatsAppMessaging).UnitsUsed);
    }

    [Fact]
    public async Task The_ledger_and_the_summary_can_be_limited_to_a_range_of_days()
    {
        await GrantPlanAsync();
        await SpendAsync(300, "m1");
        var spentOn = DateOnly.FromDateTime(_clock.UtcNow);

        var inside = await _service.GetLedgerAsync(_tenant, new QuotaLedgerQuery { From = spentOn, To = spentOn, EntryType = QuotaEntryType.Consumption, PageSize = 50 });
        var before = await _service.GetLedgerAsync(_tenant, new QuotaLedgerQuery { To = spentOn.AddDays(-1), EntryType = QuotaEntryType.Consumption, PageSize = 50 });
        var summary = await _service.GetLedgerSummaryAsync(_tenant, 30, from: spentOn, to: spentOn);
        var elsewhere = await _service.GetLedgerSummaryAsync(_tenant, 30, from: spentOn.AddDays(-10), to: spentOn.AddDays(-5));

        Assert.Equal(-300m, Assert.Single(inside.Items).UnitsDelta);
        Assert.Empty(before.Items);
        Assert.Single(summary.Daily);
        Assert.Equal(300m, summary.Daily.Sum(d => d.Used));
        Assert.Equal(6, elsewhere.Daily.Count);
        Assert.Equal(0m, elsewhere.Daily.Sum(d => d.Used));
        Assert.Empty(elsewhere.ByFeature);
    }

    [Fact]
    public async Task Same_operation_key_never_charges_twice()
    {
        await GrantPlanAsync();
        await SpendAsync(300, "m1");
        var again = await SpendAsync(300, "m1");

        Assert.True(again.AlreadyApplied);
        Assert.Equal(700m, await BalanceAsync());
    }

    [Fact]
    public async Task Credits_that_expire_are_spent_before_plan_credits_and_a_spend_can_span_both()
    {
        await GrantPlanAsync();
        await _service.GrantCreditsAsync(_tenant, Pack(), Guid.NewGuid(), _clock.UtcNow);

        await SpendAsync(1200, "big");

        var live = (await _service.GetBalancesAsync(_tenant)).Single();
        Assert.Equal(800m, live.Balance);
        var only = Assert.Single(live.Grants);
        Assert.Equal(QuotaGrantOrigin.PlanAllocation, only.Origin); // the purchased ones, which lapse, went first
        Assert.Equal(2, await _db.QuotaLedgerEntries.IgnoreQueryFilters().CountAsync(e => e.EntryType == QuotaEntryType.Consumption));
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task A_short_balance_refuses_the_spend_unless_partial_is_allowed()
    {
        await GrantPlanAsync();

        var refused = await SpendAsync(1500, "too-big");
        Assert.False(refused.Sufficient);
        Assert.Equal(0m, refused.Consumed);
        Assert.Equal(1000m, await BalanceAsync());

        var partial = await SpendAsync(1500, "shrinks", partial: true);
        Assert.False(partial.Sufficient);
        Assert.Equal(1000m, partial.Consumed);
        Assert.Equal(0m, await BalanceAsync());
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Reversal_returns_the_units_once_to_the_same_feature()
    {
        await GrantPlanAsync();
        await SpendAsync(300, "m1");

        Assert.True(await _service.ReverseConsumptionAsync(_tenant, "m1"));
        Assert.True(await _service.ReverseConsumptionAsync(_tenant, "m1"));

        Assert.Equal(1000m, await BalanceAsync());
        Assert.False(await _service.ReverseConsumptionAsync(_tenant, "never-spent"));
        var giveBack = await _db.QuotaLedgerEntries.IgnoreQueryFilters().SingleAsync(e => e.EntryType == QuotaEntryType.ConsumptionReversal);
        Assert.Equal(PlatformFeature.WhatsAppMessaging, giveBack.Feature);
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Purchased_credits_are_valid_for_twelve_months()
    {
        await _service.GrantCreditsAsync(_tenant, Pack(), Guid.NewGuid(), _clock.UtcNow);

        var grant = await _db.QuotaGrants.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_clock.UtcNow.AddMonths(12), grant.ExpiresAtUtc);

        _clock.UtcNow = _clock.UtcNow.AddMonths(11);
        Assert.Equal(1000m, await BalanceAsync());
        _clock.UtcNow = _clock.UtcNow.AddMonths(2);
        Assert.Equal(0m, await BalanceAsync());

        Assert.True(await _service.ExpireDueAsync() >= 1);
        var expiry = await _db.QuotaLedgerEntries.IgnoreQueryFilters().SingleAsync(e => e.EntryType == QuotaEntryType.Expiry);
        Assert.Equal(-1000m, expiry.UnitsDelta);
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Buying_the_same_payment_twice_grants_once()
    {
        var payment = Guid.NewGuid();
        await _service.GrantCreditsAsync(_tenant, Pack(), payment, _clock.UtcNow);
        await _service.GrantCreditsAsync(_tenant, Pack(), payment, _clock.UtcNow);

        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task Adjustments_need_a_reason_and_cannot_remove_more_than_exists()
    {
        await GrantPlanAsync();
        var admin = Guid.NewGuid();

        await _service.AdjustAsync(_tenant, QuotaType.Credits, 50, "goodwill", admin);
        Assert.Equal(1050m, await BalanceAsync());
        await _service.AdjustAsync(_tenant, QuotaType.Credits, -100, "correction", admin);
        Assert.Equal(950m, await BalanceAsync());

        await Assert.ThrowsAsync<ConflictException>(() => _service.AdjustAsync(_tenant, QuotaType.Credits, -5000, "too much", admin));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.AdjustAsync(_tenant, QuotaType.Credits, 10, " ", admin));
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task An_adjustment_of_an_older_quota_type_adjusts_the_one_wallet()
    {
        await _service.AdjustAsync(_tenant, QuotaType.WhatsAppMessages, 40, "goodwill", Guid.NewGuid());

        Assert.Equal(40m, await BalanceAsync());
    }

    [Fact]
    public async Task Clawback_only_removes_what_is_still_unspent()
    {
        var payment = Guid.NewGuid();
        await _service.GrantCreditsAsync(_tenant, Pack(), payment, _clock.UtcNow);
        await SpendAsync(300, "a1", feature: PlatformFeature.AiConversations);

        var taken = await _service.ClawBackCreditsAsync(_tenant, payment, 1000, "refund", null);

        Assert.Equal(700m, taken);
        Assert.Equal(0m, await BalanceAsync());
        await AssertLedgerMatchesGrantsAsync();
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
}
