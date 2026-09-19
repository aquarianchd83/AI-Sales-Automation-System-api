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

public sealed class QuotaLedgerServiceTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeClock _clock = new() { UtcNow = Start.AddDays(11) };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _service;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _plan = Guid.NewGuid();

    public QuotaLedgerServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();
        _service = new QuotaLedgerService(_db, _clock);

        _db.Plans.Add(new Plan { Id = _plan, Code = "starter", Name = "Starter", PriceMonthlyCents = 3900 });
        _db.PlanQuotas.Add(new PlanQuota { PlanId = _plan, QuotaType = QuotaType.WhatsAppMessages, IncludedUnits = 1000 });
        _db.PlanQuotas.Add(new PlanQuota { PlanId = _plan, QuotaType = QuotaType.AiConversations, IncludedUnits = 500 });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task AllocateAsync(DateTime? periodStart = null) =>
        _service.AllocatePlanQuotaAsync(_tenant, _plan, periodStart ?? Start, (periodStart ?? Start).AddMonths(1), Guid.NewGuid());

    private Task<ConsumeResult> SpendAsync(decimal units, string key, QuotaType type = QuotaType.WhatsAppMessages, bool partial = false) =>
        _service.ConsumeAsync(new ConsumeRequest(_tenant, type, units, key, "Message", key, AllowPartial: partial));

    private async Task<decimal> BalanceAsync(QuotaType type = QuotaType.WhatsAppMessages) =>
        (await _service.GetBalancesAsync(_tenant)).Single(b => b.QuotaType == type).Balance;

    private static CreditPack Pack(QuotaType type) =>
        new() { QuotaType = type, Name = "pack", Units = 1000, PriceCents = 2000 };

    // The invariant the whole design rests on: what the ledger sums to is exactly what is left in the grants.
    private async Task AssertLedgerMatchesGrantsAsync(QuotaType type = QuotaType.WhatsAppMessages)
    {
        var ledger = (await _db.QuotaLedgerEntries.IgnoreQueryFilters().Where(e => e.TenantId == _tenant && e.QuotaType == type).ToListAsync()).Sum(e => e.UnitsDelta);
        var grants = (await _db.QuotaGrants.IgnoreQueryFilters().Where(g => g.TenantId == _tenant && g.QuotaType == type).ToListAsync()).Sum(g => g.UnitsRemaining);
        Assert.Equal(grants, ledger);
    }

    [Fact]
    public async Task Allocation_grants_every_plan_quota_and_is_idempotent()
    {
        await AllocateAsync();
        await AllocateAsync();

        var balances = await _service.GetBalancesAsync(_tenant);
        Assert.Equal(1000m, balances.Single(b => b.QuotaType == QuotaType.WhatsAppMessages).Balance);
        Assert.Equal(500m, balances.Single(b => b.QuotaType == QuotaType.AiConversations).Balance);
        Assert.Equal(0m, balances.Single(b => b.QuotaType == QuotaType.LeadCandidates).Balance);
        Assert.Equal(2, await _db.QuotaLedgerEntries.IgnoreQueryFilters().CountAsync());
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Consumption_reduces_balance_and_records_running_balance()
    {
        await AllocateAsync();
        var result = await SpendAsync(300, "m1");

        Assert.True(result.Sufficient);
        Assert.Equal(700m, result.BalanceAfter);
        Assert.Equal(700m, await BalanceAsync());
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Same_operation_key_never_charges_twice()
    {
        await AllocateAsync();
        await SpendAsync(300, "m1");
        var again = await SpendAsync(300, "m1");

        Assert.True(again.AlreadyApplied);
        Assert.Equal(700m, await BalanceAsync());
    }

    [Fact]
    public async Task Included_quota_is_spent_before_purchased_credits_and_a_spend_can_span_both()
    {
        await AllocateAsync();
        await _service.GrantCreditsAsync(_tenant, Pack(QuotaType.WhatsAppMessages), Guid.NewGuid(), _clock.UtcNow);

        await SpendAsync(1200, "big");

        var live = (await _service.GetBalancesAsync(_tenant)).Single(b => b.QuotaType == QuotaType.WhatsAppMessages);
        Assert.Equal(800m, live.Balance);
        var only = Assert.Single(live.Grants);
        Assert.Equal(QuotaGrantOrigin.CreditPurchase, only.Origin);
        Assert.Equal(2, await _db.QuotaLedgerEntries.IgnoreQueryFilters().CountAsync(e => e.EntryType == QuotaEntryType.Consumption));
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task A_short_balance_refuses_the_spend_unless_partial_is_allowed()
    {
        await AllocateAsync();

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
    public async Task Reversal_returns_the_units_once()
    {
        await AllocateAsync();
        await SpendAsync(300, "m1");

        Assert.True(await _service.ReverseConsumptionAsync(_tenant, "m1"));
        Assert.True(await _service.ReverseConsumptionAsync(_tenant, "m1"));

        Assert.Equal(1000m, await BalanceAsync());
        Assert.False(await _service.ReverseConsumptionAsync(_tenant, "never-spent"));
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Unused_plan_quota_expires_at_period_end_and_credits_do_not()
    {
        await AllocateAsync();
        await _service.GrantCreditsAsync(_tenant, Pack(QuotaType.WhatsAppMessages), Guid.NewGuid(), _clock.UtcNow);
        await SpendAsync(400, "m1");

        _clock.UtcNow = Start.AddMonths(1).AddDays(1);
        Assert.Equal(1000m, await BalanceAsync());

        var processed = await _service.ExpireDueAsync();
        Assert.True(processed >= 1);
        Assert.Equal(1000m, await BalanceAsync());
        var expiry = await _db.QuotaLedgerEntries.IgnoreQueryFilters().SingleAsync(e => e.EntryType == QuotaEntryType.Expiry && e.QuotaType == QuotaType.WhatsAppMessages);
        Assert.Equal(-600m, expiry.UnitsDelta);
        Assert.Equal(0, await _service.ExpireDueAsync());
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Credits_are_valid_for_twelve_months()
    {
        await _service.GrantCreditsAsync(_tenant, Pack(QuotaType.AiConversations), Guid.NewGuid(), _clock.UtcNow);

        var grant = await _db.QuotaGrants.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_clock.UtcNow.AddMonths(12), grant.ExpiresAtUtc);

        _clock.UtcNow = _clock.UtcNow.AddMonths(11);
        Assert.Equal(1000m, await BalanceAsync(QuotaType.AiConversations));
        _clock.UtcNow = _clock.UtcNow.AddMonths(2);
        Assert.Equal(0m, await BalanceAsync(QuotaType.AiConversations));
    }

    [Fact]
    public async Task Buying_the_same_payment_twice_grants_once()
    {
        var payment = Guid.NewGuid();
        await _service.GrantCreditsAsync(_tenant, Pack(QuotaType.AiConversations), payment, _clock.UtcNow);
        await _service.GrantCreditsAsync(_tenant, Pack(QuotaType.AiConversations), payment, _clock.UtcNow);

        Assert.Equal(1000m, await BalanceAsync(QuotaType.AiConversations));
    }

    [Fact]
    public async Task Adjustments_need_a_reason_and_cannot_remove_more_than_exists()
    {
        await AllocateAsync();
        var admin = Guid.NewGuid();

        await _service.AdjustAsync(_tenant, QuotaType.WhatsAppMessages, 50, "goodwill", admin);
        Assert.Equal(1050m, await BalanceAsync());
        await _service.AdjustAsync(_tenant, QuotaType.WhatsAppMessages, -100, "correction", admin);
        Assert.Equal(950m, await BalanceAsync());

        await Assert.ThrowsAsync<ConflictException>(() => _service.AdjustAsync(_tenant, QuotaType.WhatsAppMessages, -5000, "too much", admin));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.AdjustAsync(_tenant, QuotaType.WhatsAppMessages, 10, " ", admin));
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Trial_quota_is_granted_once_and_lapses_with_the_trial()
    {
        var trialEnds = _clock.UtcNow.AddDays(14);
        var units = new Dictionary<QuotaType, decimal> { [QuotaType.WhatsAppMessages] = 50, [QuotaType.LeadCandidates] = 10 };

        await _service.GrantTrialQuotaAsync(_tenant, units, trialEnds);
        await _service.GrantTrialQuotaAsync(_tenant, units, trialEnds);

        Assert.Equal(50m, await BalanceAsync());
        Assert.Equal(10m, await BalanceAsync(QuotaType.LeadCandidates));
        Assert.Equal(0m, await BalanceAsync(QuotaType.AiConversations));

        _clock.UtcNow = trialEnds.AddHours(1);
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task Choosing_a_plan_forfeits_the_old_periods_included_quota_but_keeps_credits()
    {
        await AllocateAsync();
        await _service.GrantCreditsAsync(_tenant, Pack(QuotaType.WhatsAppMessages), Guid.NewGuid(), _clock.UtcNow);

        var forfeited = await _service.ForfeitPlanAllocationsAsync(_tenant, "Replaced by a new plan period");

        Assert.Equal(1500m, forfeited); // 1000 WhatsApp + 500 AI included units
        Assert.Equal(1000m, await BalanceAsync());
        Assert.Equal(0m, await BalanceAsync(QuotaType.AiConversations));
        await AssertLedgerMatchesGrantsAsync();
    }

    [Fact]
    public async Task Clawback_only_removes_what_is_still_unspent()
    {
        var payment = Guid.NewGuid();
        await _service.GrantCreditsAsync(_tenant, Pack(QuotaType.AiConversations), payment, _clock.UtcNow);
        await SpendAsync(300, "a1", QuotaType.AiConversations);

        var taken = await _service.ClawBackCreditsAsync(_tenant, payment, 1000, "refund", null);

        Assert.Equal(700m, taken);
        Assert.Equal(0m, await BalanceAsync(QuotaType.AiConversations));
        await AssertLedgerMatchesGrantsAsync(QuotaType.AiConversations);
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
