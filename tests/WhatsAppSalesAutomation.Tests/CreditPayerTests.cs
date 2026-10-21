using FluentValidation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public sealed class CreditPayerTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeClock _clock = new() { UtcNow = Now };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _tenantLedger;
    private readonly CreditRouter _router;
    private readonly CreditPayerService _service;
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    public CreditPayerTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();
        _tenantLedger = new QuotaLedgerService(_db, _clock);
        _router = new CreditRouter(_db, _tenantLedger, _clock, NullLogger<CreditRouter>.Instance);
        _service = new CreditPayerService(_db, _clock, _tenantLedger);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task GiveTenantAsync(decimal units, QuotaType type = QuotaType.AiConversations) =>
        _tenantLedger.AdjustAsync(_tenant, type, units, "seed", _admin);

    private Task FundPlatformAsync(decimal units, QuotaType type = QuotaType.AiConversations) =>
        _service.AdjustWalletAsync(new AdjustPlatformWalletRequest(type, units, "top up"), _admin);

    private Task PayerAsync(CreditScenario scenario, CreditPayer payer, decimal? units = null) =>
        _service.SetPayerAsync(scenario, new UpdateCreditPayerRequest(payer, units), _admin);

    private Task<ConsumeResult> ChatReplyAsync(string key, decimal units = 1m) =>
        _router.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.AiConversations, units, key, "AiTransaction", key));

    private async Task<decimal> TenantBalanceAsync(QuotaType type = QuotaType.AiConversations) =>
        (await _tenantLedger.GetBalancesAsync(_tenant)).Single(b => b.QuotaType == type).Balance;

    private async Task<decimal> PlatformBalanceAsync(QuotaType type = QuotaType.AiConversations) =>
        (await _service.GetWalletsAsync()).Single(w => w.QuotaType == type).Balance;

    [Fact]
    public async Task By_default_the_tenant_pays_and_the_spend_is_recorded()
    {
        await GiveTenantAsync(10);
        await FundPlatformAsync(10);

        var result = await ChatReplyAsync("chat-1");

        Assert.True(result.Sufficient);
        Assert.Equal(9m, await TenantBalanceAsync());
        Assert.Equal(10m, await PlatformBalanceAsync());
        var usage = await _db.CreditUsages.SingleAsync();
        Assert.Equal(CreditScenario.CustomerChatReply, usage.Scenario);
        Assert.Equal(CreditPayer.Tenant, usage.ChargedTo);
        Assert.False(usage.FellBackToTenant);
    }

    [Fact]
    public async Task When_the_platform_pays_its_wallet_is_charged_and_the_tenant_is_not()
    {
        await GiveTenantAsync(10);
        await FundPlatformAsync(5);
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Platform);

        var result = await ChatReplyAsync("chat-1");

        Assert.True(result.Sufficient);
        Assert.Equal(10m, await TenantBalanceAsync());
        Assert.Equal(4m, await PlatformBalanceAsync());
        var usage = await _db.CreditUsages.SingleAsync();
        Assert.Equal(CreditPayer.Platform, usage.ChargedTo);
        Assert.Equal(CreditPayer.Platform, usage.ConfiguredPayer);
        Assert.Equal(-1m, (await _db.PlatformCreditEntries.SingleAsync(e => e.EntryType == PlatformCreditEntryType.Consumption)).UnitsDelta);
    }

    [Fact]
    public async Task An_empty_platform_wallet_falls_back_to_the_tenant_and_says_so()
    {
        await GiveTenantAsync(10);
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Platform);

        var result = await ChatReplyAsync("chat-1");

        Assert.True(result.Sufficient);
        Assert.Equal(9m, await TenantBalanceAsync());
        Assert.Equal(0m, await PlatformBalanceAsync());
        var usage = await _db.CreditUsages.SingleAsync();
        Assert.Equal(CreditPayer.Platform, usage.ConfiguredPayer);
        Assert.Equal(CreditPayer.Tenant, usage.ChargedTo);
        Assert.True(usage.FellBackToTenant);
        Assert.Empty(_db.PlatformCreditWallets.Local.Where(w => w.Balance == 0 && w.CreatedAt == default));
    }

    [Fact]
    public async Task When_neither_account_can_pay_the_spend_is_refused_and_nothing_is_recorded()
    {
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Platform);

        var result = await ChatReplyAsync("chat-1");

        Assert.False(result.Sufficient);
        Assert.Empty(await _db.CreditUsages.ToListAsync());
    }

    [Fact]
    public async Task A_retried_spend_charges_the_platform_once()
    {
        await FundPlatformAsync(5);
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Platform);

        await ChatReplyAsync("chat-1");
        var again = await ChatReplyAsync("chat-1");

        Assert.True(again.AlreadyApplied);
        Assert.Equal(4m, await PlatformBalanceAsync());
        Assert.Single(await _db.CreditUsages.ToListAsync());
    }

    [Fact]
    public async Task Reversing_a_platform_spend_refills_the_wallet_once()
    {
        await FundPlatformAsync(5);
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Platform);
        await ChatReplyAsync("chat-1");

        Assert.True(await _router.ReverseAsync(_tenant, "chat-1"));
        Assert.True(await _router.ReverseAsync(_tenant, "chat-1"));

        Assert.Equal(5m, await PlatformBalanceAsync());
        Assert.Equal(CreditUsageStatus.Reversed, (await _db.CreditUsages.SingleAsync()).Status);
    }

    [Fact]
    public async Task Reversing_a_tenant_spend_gives_the_tenant_its_credit_back()
    {
        await GiveTenantAsync(10);
        await ChatReplyAsync("chat-1");

        await _router.ReverseAsync(_tenant, "chat-1");

        Assert.Equal(10m, await TenantBalanceAsync());
        Assert.Equal(CreditUsageStatus.Reversed, (await _db.CreditUsages.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_spend_with_no_scenario_goes_straight_to_the_tenant_unrecorded()
    {
        await GiveTenantAsync(10);

        var result = await _router.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.AiConversations, 2m, "x-1", "SomethingElse", "x-1"));

        Assert.True(result.Sufficient);
        Assert.Equal(8m, await TenantBalanceAsync());
        Assert.Empty(await _db.CreditUsages.ToListAsync());
    }

    [Fact]
    public async Task The_ledger_the_app_sees_routes_through_the_payer_setting()
    {
        await FundPlatformAsync(3, QuotaType.WhatsAppMessages);
        await PayerAsync(CreditScenario.WhatsAppTemplateSend, CreditPayer.Platform);
        IQuotaLedgerService ledger = new CreditRoutingLedger(_tenantLedger, _router);

        await ledger.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.WhatsAppMessages, 1m, "wa-1", "Message", "wa-1"));

        Assert.Equal(2m, await PlatformBalanceAsync(QuotaType.WhatsAppMessages));
        Assert.Equal(CreditScenario.WhatsAppTemplateSend, (await _db.CreditUsages.SingleAsync()).Scenario);
    }

    [Fact]
    public async Task Redeeming_gives_the_tenant_its_credits_back_once()
    {
        await GiveTenantAsync(10);
        await ChatReplyAsync("chat-1", 2m);
        var usage = await _db.CreditUsages.SingleAsync();

        var redeemed = await _service.RedeemToTenantAsync(usage.Id, "Charged by mistake", _admin);

        Assert.Equal(CreditUsageStatus.Redeemed, redeemed.Status);
        Assert.Equal(10m, await TenantBalanceAsync());
        await Assert.ThrowsAsync<ConflictException>(() => _service.RedeemToTenantAsync(usage.Id, "again", _admin));
        Assert.Equal(10m, await TenantBalanceAsync());

        // A later automatic reversal must not give them a second refund.
        await _router.ReverseAsync(_tenant, "chat-1");
        Assert.Equal(10m, await TenantBalanceAsync());
    }

    [Fact]
    public async Task Redeeming_needs_a_note_and_a_tenant_paid_spend()
    {
        await FundPlatformAsync(5);
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Platform);
        await ChatReplyAsync("chat-1");
        var usage = await _db.CreditUsages.SingleAsync();

        await Assert.ThrowsAsync<ValidationException>(() => _service.RedeemToTenantAsync(usage.Id, "  ", _admin));
        await Assert.ThrowsAsync<ConflictException>(() => _service.RedeemToTenantAsync(usage.Id, "note", _admin));
    }

    [Fact]
    public async Task Product_knowledge_is_free_until_credits_per_action_are_set()
    {
        await GiveTenantAsync(10);

        await _router.ChargeActionAsync(_tenant, CreditScenario.ProductKnowledgeReading, "pk:1", "DocumentAnalysis");

        Assert.Equal(10m, await TenantBalanceAsync());
        Assert.Empty(await _db.CreditUsages.ToListAsync());
    }

    [Fact]
    public async Task Product_knowledge_charges_the_platform_wallet_once_credits_per_action_are_set()
    {
        await GiveTenantAsync(10);
        await FundPlatformAsync(5);
        await PayerAsync(CreditScenario.ProductKnowledgeReading, CreditPayer.Platform, 2m);

        await _router.ChargeActionAsync(_tenant, CreditScenario.ProductKnowledgeReading, "pk:1", "DocumentAnalysis");

        Assert.Equal(3m, await PlatformBalanceAsync());
        Assert.Equal(10m, await TenantBalanceAsync());
        var usage = await _db.CreditUsages.SingleAsync();
        Assert.Equal(2m, usage.Units);
        Assert.Equal("DocumentAnalysis", usage.Description);
    }

    [Fact]
    public async Task Product_knowledge_work_nobody_can_pay_for_is_recorded_as_unpaid_not_refused()
    {
        await PayerAsync(CreditScenario.ProductKnowledgeAnswering, CreditPayer.Platform, 1m);

        await _router.ChargeActionAsync(_tenant, CreditScenario.ProductKnowledgeAnswering, "pk:2", "GroundedQuestionAnswering");

        var usage = await _db.CreditUsages.SingleAsync();
        Assert.Equal(CreditUsageStatus.Unpaid, usage.Status);
        Assert.Equal(0m, await PlatformBalanceAsync());
    }

    [Fact]
    public async Task Switching_the_payer_only_changes_later_spends()
    {
        await GiveTenantAsync(10);
        await FundPlatformAsync(5);

        await ChatReplyAsync("chat-1");
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Platform);
        await ChatReplyAsync("chat-2");
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Tenant);
        await ChatReplyAsync("chat-3");

        var payers = (await _db.CreditUsages.OrderBy(u => u.OccurredAtUtc).ThenBy(u => u.OperationKey).ToListAsync()).ToDictionary(u => u.OperationKey, u => u.ChargedTo);
        Assert.Equal(CreditPayer.Tenant, payers["chat-1"]);
        Assert.Equal(CreditPayer.Platform, payers["chat-2"]);
        Assert.Equal(CreditPayer.Tenant, payers["chat-3"]);
        Assert.Equal(8m, await TenantBalanceAsync());
        Assert.Equal(4m, await PlatformBalanceAsync());
    }

    [Fact]
    public async Task Units_per_action_only_apply_to_scenarios_that_let_the_platform_set_them()
    {
        var chat = await _service.SetPayerAsync(CreditScenario.CustomerChatReply, new UpdateCreditPayerRequest(CreditPayer.Platform, 9m), _admin);
        var knowledge = await _service.SetPayerAsync(CreditScenario.ProductKnowledgeReading, new UpdateCreditPayerRequest(CreditPayer.Tenant, 3m), _admin);

        Assert.Null(chat.UnitsPerAction);
        Assert.Equal(3m, knowledge.UnitsPerAction);
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SetPayerAsync(CreditScenario.ProductKnowledgeReading, new UpdateCreditPayerRequest(CreditPayer.Tenant, -1m), _admin));
    }

    [Fact]
    public async Task The_platform_wallet_cannot_go_below_zero_and_needs_a_reason()
    {
        await FundPlatformAsync(5);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _service.AdjustWalletAsync(new AdjustPlatformWalletRequest(QuotaType.AiConversations, -6m, "oops"), _admin));
        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.AdjustWalletAsync(new AdjustPlatformWalletRequest(QuotaType.AiConversations, 5m, " "), _admin));

        await _service.AdjustWalletAsync(new AdjustPlatformWalletRequest(QuotaType.AiConversations, -2m, "correction"), _admin);
        Assert.Equal(3m, await PlatformBalanceAsync());
    }

    [Fact]
    public async Task The_scenario_list_summarises_who_paid_over_the_last_30_days()
    {
        await GiveTenantAsync(10);
        await FundPlatformAsync(5);
        await ChatReplyAsync("chat-1");
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Platform);
        await ChatReplyAsync("chat-2");

        var chat = (await _service.GetScenariosAsync()).Single(s => s.Scenario == CreditScenario.CustomerChatReply);

        Assert.Equal(CreditPayer.Platform, chat.Payer);
        Assert.Equal(2, chat.ActionsLast30Days);
        Assert.Equal(1m, chat.UnitsChargedToTenantsLast30Days);
        Assert.Equal(1m, chat.UnitsChargedToPlatformLast30Days);
        Assert.Equal(4m, chat.PlatformBalance);
        Assert.Equal(CreditScenarioCatalog.All.Count, (await _service.GetScenariosAsync()).Count);
    }

    [Fact]
    public async Task A_tenant_sees_what_the_platform_covered_but_not_unpaid_bookkeeping()
    {
        await FundPlatformAsync(5);
        await GiveTenantAsync(2);
        await ChatReplyAsync("chat-1");
        await PayerAsync(CreditScenario.CustomerChatReply, CreditPayer.Platform);
        await ChatReplyAsync("chat-2");

        // Both accounts run dry, so the next knowledge action is done but nobody can be charged for it.
        await PayerAsync(CreditScenario.ProductKnowledgeAnswering, CreditPayer.Platform, 1m);
        await _service.AdjustWalletAsync(new AdjustPlatformWalletRequest(QuotaType.AiConversations, -4m, "drain"), _admin);
        await _router.ConsumeAsync(new ConsumeRequest(_tenant, QuotaType.AiConversations, 1m, "other-1", "SomethingElse", "other-1"));
        await _router.ChargeActionAsync(_tenant, CreditScenario.ProductKnowledgeAnswering, "pk:9", "KnowledgeDraft");
        Assert.Equal(CreditUsageStatus.Unpaid, (await _db.CreditUsages.SingleAsync(u => u.OperationKey == "pk:9")).Status);

        var all = await _service.GetTenantUsageAsync(_tenant, new TenantCreditUsageQuery());
        var covered = await _service.GetTenantUsageAsync(_tenant, new TenantCreditUsageQuery { CoveredByPlatform = true });

        Assert.Equal(2, all.TotalCount);
        Assert.Single(covered.Items);
        Assert.True(covered.Items[0].CoveredByPlatform);
        Assert.Empty((await _service.GetTenantUsageAsync(Guid.NewGuid(), new TenantCreditUsageQuery())).Items);
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
