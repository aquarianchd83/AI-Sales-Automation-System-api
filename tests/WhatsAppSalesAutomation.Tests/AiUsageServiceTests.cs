using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Ai;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The AI gate: subscription state, then credit, then an audit row - and the credit coming back when the
/// platform fails.</summary>
public sealed class AiUsageServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FixedConfig : IPlatformAiConfigProvider
    {
        public AiCredentials Get() => new("OpenAI", "OpenAI", null, "m", "v", "u", "key", "gpt-test", "emb", "u", null, "g", "ge", "u");
    }

    private sealed class RecordingAlerts : IQuotaAlertService
    {
        public int ExhaustedCalls { get; private set; }

        public Task<bool> NotifyIfExhaustedAsync(Guid tenantId, QuotaType quotaType, CancellationToken cancellationToken = default)
        {
            ExhaustedCalls++;
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

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeClock _clock = new() { UtcNow = Now };
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly RecordingAlerts _alerts = new();
    private readonly AiUsageService _service;
    private readonly Guid _tenant = Guid.NewGuid();

    public AiUsageServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new FakeTenantContext(), new FakeCurrentUser());
        _db.Database.EnsureCreated();
        _ledger = new QuotaLedgerService(_db, _clock);
        _service = new AiUsageService(_db, _ledger, _alerts, _clock, new FixedConfig(), NullLogger<AiUsageService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task GivenTenantAsync(TenantStatus status, DateTime? trialEnds = null, SubscriptionStatus? subscription = null, DateTime? periodEnd = null)
    {
        _db.Tenants.Add(new Tenant { Id = _tenant, Name = "T", Slug = "t", Status = status, TrialEndsAtUtc = trialEnds });
        if (subscription is { } s)
            _db.Subscriptions.Add(new Subscription { TenantId = _tenant, Status = s, CurrentPeriodEndUtc = periodEnd });
        await _db.SaveChangesAsync();
    }

    private Task GrantAsync(decimal units, QuotaGrantOrigin origin = QuotaGrantOrigin.CreditPurchase) =>
        origin == QuotaGrantOrigin.Trial
            ? _ledger.GrantTrialQuotaAsync(_tenant, new Dictionary<QuotaType, decimal> { [QuotaType.AiConversations] = units }, Now.AddDays(10))
            : _ledger.GrantCreditsAsync(_tenant, new CreditPack { QuotaType = QuotaType.AiConversations, Name = "p", Units = units, PriceCents = 100 }, Guid.NewGuid(), Now);

    private async Task<decimal> BalanceAsync() =>
        (await _ledger.GetBalancesAsync(_tenant)).Single(b => b.QuotaType == QuotaType.AiConversations).Balance;

    [Fact]
    public async Task Paid_tenant_with_credit_is_charged_one_and_audited()
    {
        await GivenTenantAsync(TenantStatus.Active, subscription: SubscriptionStatus.Active, periodEnd: Now.AddDays(10));
        await GrantAsync(5);

        var result = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");

        Assert.True(result.Allowed);
        Assert.Equal(4m, await BalanceAsync());
        var row = await _db.AiTransactions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AiTransactionStatus.Authorized, row.Status);
        Assert.Equal(AiUsageSource.Paid, row.Source);
        Assert.Equal((5m, 1m, 4m), (row.CreditsBefore, row.CreditsConsumed, row.CreditsAfter));
        Assert.Equal("OpenAI", row.Provider);
        Assert.Equal("gpt-test", row.Model);
    }

    [Fact]
    public async Task Paid_tenant_without_credit_is_blocked_with_the_buy_credits_message_and_notified()
    {
        await GivenTenantAsync(TenantStatus.Active, subscription: SubscriptionStatus.Active, periodEnd: Now.AddDays(10));

        var result = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");

        Assert.False(result.Allowed);
        Assert.Equal(AiDenialReason.InsufficientCredits, result.Reason);
        Assert.Equal(AiUsageService.InsufficientCreditsMessage, result.Message);
        Assert.Equal(1, _alerts.ExhaustedCalls);
        var row = await _db.AiTransactions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AiTransactionStatus.Blocked, row.Status);
        Assert.Equal(0m, row.CreditsConsumed);
    }

    [Fact]
    public async Task Trial_tenant_uses_its_trial_allowance_and_is_blocked_when_it_runs_out()
    {
        await GivenTenantAsync(TenantStatus.Trial, trialEnds: Now.AddDays(5));
        await GrantAsync(1, QuotaGrantOrigin.Trial);

        var first = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");
        var second = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-2");

        Assert.True(first.Allowed);
        Assert.False(second.Allowed);
        Assert.Equal(AiDenialReason.TrialLimitReached, second.Reason);
        Assert.Equal(AiUsageSource.Trial, (await _db.AiTransactions.IgnoreQueryFilters().FirstAsync(t => t.ReferenceId == "msg-1")).Source);
    }

    [Fact]
    public async Task Expired_trial_is_blocked_even_with_credit_left()
    {
        await GivenTenantAsync(TenantStatus.Trial, trialEnds: Now.AddDays(-1));
        await GrantAsync(5);

        var result = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");

        Assert.False(result.Allowed);
        Assert.Equal(AiDenialReason.TrialExpired, result.Reason);
        Assert.Equal(5m, await BalanceAsync());
    }

    [Theory]
    [InlineData(TenantStatus.Suspended, null, null, AiDenialReason.AccountInactive)]
    [InlineData(TenantStatus.Deleted, null, null, AiDenialReason.AccountInactive)]
    [InlineData(TenantStatus.Cancelled, null, null, AiDenialReason.SubscriptionCancelled)]
    [InlineData(TenantStatus.Active, SubscriptionStatus.Canceled, 10, AiDenialReason.SubscriptionCancelled)]
    [InlineData(TenantStatus.Active, SubscriptionStatus.PastDue, 10, AiDenialReason.SubscriptionExpired)]
    [InlineData(TenantStatus.Active, SubscriptionStatus.Active, -1, AiDenialReason.SubscriptionExpired)]
    public async Task Inactive_or_expired_subscriptions_are_blocked_before_any_credit_is_touched(
        TenantStatus status, SubscriptionStatus? subscription, int? periodEndDays, AiDenialReason expected)
    {
        await GivenTenantAsync(status, subscription: subscription, periodEnd: periodEndDays is { } d ? Now.AddDays(d) : null);
        await GrantAsync(5);

        var result = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");

        Assert.False(result.Allowed);
        Assert.Equal(expected, result.Reason);
        Assert.Equal(5m, await BalanceAsync());
    }

    [Fact]
    public async Task Retrying_an_authorised_operation_does_not_charge_twice()
    {
        await GivenTenantAsync(TenantStatus.Active, subscription: SubscriptionStatus.Active, periodEnd: Now.AddDays(10));
        await GrantAsync(5);

        var first = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");
        var again = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");

        Assert.Equal(first.TransactionId, again.TransactionId);
        Assert.Equal(4m, await BalanceAsync());
        Assert.Equal(1, await _db.AiTransactions.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_platform_failure_gives_the_credit_back_and_a_retry_is_charged_afresh()
    {
        await GivenTenantAsync(TenantStatus.Active, subscription: SubscriptionStatus.Active, periodEnd: Now.AddDays(10));
        await GrantAsync(5);

        var first = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");
        await _service.FailAsync(first.TransactionId!.Value, "provider returned 503", "OpenAI", "gpt-test");

        Assert.Equal(5m, await BalanceAsync());
        var failed = await _db.AiTransactions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AiTransactionStatus.Failed, failed.Status);
        Assert.Equal(1m, failed.CreditsRefunded);
        Assert.Equal(5m, failed.CreditsAfter);
        Assert.Equal("provider returned 503", failed.FailureReason);

        var retry = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");

        Assert.True(retry.Allowed);
        Assert.NotEqual(first.TransactionId, retry.TransactionId);
        Assert.Equal(4m, await BalanceAsync());
    }

    [Fact]
    public async Task A_completed_call_keeps_the_credit_and_records_the_outcome()
    {
        await GivenTenantAsync(TenantStatus.Active, subscription: SubscriptionStatus.Active, periodEnd: Now.AddDays(10));
        await GrantAsync(5);

        var auth = await _service.AuthorizeAsync(_tenant, "ConversationReply", "msg-1");
        await _service.CompleteAsync(auth.TransactionId!.Value, "OpenAI", "gpt-test", 120, 30);
        await _service.FailAsync(auth.TransactionId.Value, "too late to fail", null, null);

        var row = await _db.AiTransactions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(AiTransactionStatus.Completed, row.Status);
        Assert.Equal((120, 30), (row.PromptTokens, row.CompletionTokens));
        Assert.NotNull(row.CompletedAtUtc);
        Assert.Equal(4m, await BalanceAsync());
    }
}
