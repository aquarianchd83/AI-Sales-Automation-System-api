using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Billing.Refunds;
using WhatsAppSalesAutomation.Application.Common.Exceptions;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Quota;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;
using Xunit;

namespace WhatsAppSalesAutomation.Tests;

public sealed class RefundServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly QuotaLedgerService _ledger;
    private readonly FakeRefundGateway _gateway = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly RefundService _refunds;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme", CountryCode = "IN", RefundRequestsEnabled = true };
    private readonly Guid _tenantUser = Guid.NewGuid();
    private readonly Guid _admin = Guid.NewGuid();

    public RefundServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _ledger = new QuotaLedgerService(_db, _clock);
        _refunds = new RefundService(_db, _ledger, _gateway, _notifier, _clock, Options.Create(new RefundPolicyOptions()));

        _db.Tenants.Add(_tenant);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>A bought pack: 1,000 AI conversations for $20 (INR 1,660), with the grant behind it.</summary>
    private async Task<Payment> BuyPackAsync(DateTime? paidAt = null)
    {
        var pack = new CreditPack { QuotaType = QuotaType.AiConversations, Name = "1,000 AI conversations", Units = 1000, PriceCents = 2000 };
        var payment = new Payment
        {
            TenantId = _tenant.Id, Kind = PaymentKind.CreditPack, CreditPackId = pack.Id, PlanName = pack.Name,
            AmountCents = 2000, CurrencyCode = "INR", CurrencySymbol = "₹", LocalAmount = 1660m,
            Provider = "Simulated", PaidAtUtc = paidAt ?? _clock.UtcNow
        };
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();
        await _ledger.GrantCreditsAsync(_tenant.Id, pack, payment.Id, payment.PaidAtUtc);
        return payment;
    }

    private Task SpendAsync(decimal units, string key) =>
        _ledger.ConsumeAsync(new ConsumeRequest(_tenant.Id, QuotaType.AiConversations, units, key, "AiInteraction", key));

    private async Task<decimal> BalanceAsync() =>
        (await _ledger.GetBalancesAsync(_tenant.Id)).Single(b => b.QuotaType == QuotaType.AiConversations).Balance;

    [Fact]
    public async Task With_the_switch_off_a_tenant_can_neither_check_nor_request_a_refund()
    {
        var payment = await BuyPackAsync();
        _tenant.RefundRequestsEnabled = false;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<FeatureDisabledException>(() => _refunds.GetEligibilityAsync(_tenant.Id, payment.Id));
        await Assert.ThrowsAsync<FeatureDisabledException>(() => _refunds.RequestAsync(_tenant.Id, payment.Id, "changed my mind", _tenantUser));
        Assert.False(await _refunds.IsEnabledAsync(_tenant.Id));
        Assert.Equal(1000m, await BalanceAsync());
    }

    [Fact]
    public async Task Only_the_unspent_share_of_a_credit_pack_is_refundable_and_it_is_held_while_waiting()
    {
        var payment = await BuyPackAsync();
        await SpendAsync(300, "a1");

        var eligibility = await _refunds.GetEligibilityAsync(_tenant.Id, payment.Id);
        Assert.True(eligibility.Eligible);
        Assert.Equal(1400, eligibility.AmountCents);
        Assert.Equal(1162m, eligibility.LocalAmount);

        var request = await _refunds.RequestAsync(_tenant.Id, payment.Id, "bought too many", _tenantUser);

        Assert.Equal(RefundStatus.Requested, request.Status);
        Assert.Equal(0m, await BalanceAsync());
        Assert.Equal(0, _gateway.Calls);
    }

    [Fact]
    public async Task Nothing_is_paid_until_a_person_approves_and_then_the_refund_is_booked()
    {
        var payment = await BuyPackAsync();
        await SpendAsync(300, "a1");
        var request = await _refunds.RequestAsync(_tenant.Id, payment.Id, "bought too many", _tenantUser);

        var approved = await _refunds.ApproveAsync(request.Id, new ReviewRefundRequest("ok"), _admin);

        Assert.Equal(RefundStatus.Refunded, approved.Status);
        Assert.Equal(1400, approved.RefundedAmountCents);
        Assert.Equal(1, _gateway.Calls);
        Assert.Equal(1400, _gateway.LastAmountCents);
        Assert.Equal(0m, await BalanceAsync());

        var refundRow = await _db.Payments.IgnoreQueryFilters().SingleAsync(p => p.Kind == PaymentKind.Refund);
        Assert.Equal(-1400, refundRow.AmountCents);
        Assert.Equal(-1162m, refundRow.LocalAmount);
        Assert.Equal(payment.Id, refundRow.RefundOfPaymentId);
        Assert.Contains(_notifier.Sent, n => n.Kind == TenantNotificationKind.RefundApproved);
    }

    [Fact]
    public async Task A_partial_approval_pays_less_and_returns_the_rest_of_the_units()
    {
        var payment = await BuyPackAsync();
        var request = await _refunds.RequestAsync(_tenant.Id, payment.Id, "half is enough", _tenantUser);
        Assert.Equal(0m, await BalanceAsync());

        var approved = await _refunds.ApproveAsync(request.Id, new ReviewRefundRequest("half", ApprovedAmountCents: 1000), _admin);

        Assert.Equal(1000, approved.RefundedAmountCents);
        Assert.Equal(500m, await BalanceAsync());
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_and_gives_the_units_back()
    {
        var payment = await BuyPackAsync();
        var request = await _refunds.RequestAsync(_tenant.Id, payment.Id, "no reason", _tenantUser);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => _refunds.RejectAsync(request.Id, "  ", _admin));

        var rejected = await _refunds.RejectAsync(request.Id, "Used it for a campaign", _admin);

        Assert.Equal(RefundStatus.Rejected, rejected.Status);
        Assert.Equal(1000m, await BalanceAsync());
        Assert.Equal(0, _gateway.Calls);
        Assert.Contains(_notifier.Sent, n => n.Kind == TenantNotificationKind.RefundRejected);
    }

    [Fact]
    public async Task A_tenant_can_withdraw_a_waiting_request_and_get_its_units_back()
    {
        var payment = await BuyPackAsync();
        var request = await _refunds.RequestAsync(_tenant.Id, payment.Id, "oops", _tenantUser);

        var cancelled = await _refunds.CancelAsync(_tenant.Id, request.Id);

        Assert.Equal(RefundStatus.Cancelled, cancelled.Status);
        Assert.Equal(1000m, await BalanceAsync());
        await Assert.ThrowsAsync<ConflictException>(() => _refunds.CancelAsync(_tenant.Id, request.Id));
    }

    [Fact]
    public async Task A_gateway_failure_changes_nothing_but_the_status_and_can_be_retried()
    {
        var payment = await BuyPackAsync();
        var request = await _refunds.RequestAsync(_tenant.Id, payment.Id, "refund please", _tenantUser);
        _gateway.Succeed = false;

        var failed = await _refunds.ApproveAsync(request.Id, new ReviewRefundRequest("try"), _admin);

        Assert.Equal(RefundStatus.Failed, failed.Status);
        Assert.Equal(0m, await BalanceAsync());
        Assert.Empty(await _db.Payments.IgnoreQueryFilters().Where(p => p.Kind == PaymentKind.Refund).ToListAsync());

        _gateway.Succeed = true;
        var retried = await _refunds.ApproveAsync(request.Id, new ReviewRefundRequest("retry"), _admin);

        Assert.Equal(RefundStatus.Refunded, retried.Status);
        Assert.Single(await _db.Payments.IgnoreQueryFilters().Where(p => p.Kind == PaymentKind.Refund).ToListAsync());
    }

    [Fact]
    public async Task A_payment_can_only_be_refunded_once_and_never_after_the_window()
    {
        var payment = await BuyPackAsync();
        var first = await _refunds.RequestAsync(_tenant.Id, payment.Id, "first", _tenantUser);
        await Assert.ThrowsAsync<ConflictException>(() => _refunds.RequestAsync(_tenant.Id, payment.Id, "second", _tenantUser));
        await _refunds.ApproveAsync(first.Id, new ReviewRefundRequest("ok"), _admin);
        await Assert.ThrowsAsync<ConflictException>(() => _refunds.ApproveAsync(first.Id, new ReviewRefundRequest("again"), _admin));

        var old = await BuyPackAsync(paidAt: _clock.UtcNow.AddDays(-31));
        var late = await _refunds.GetEligibilityAsync(_tenant.Id, old.Id);
        Assert.False(late.Eligible);
        Assert.Contains("30-day", late.Reason);
    }

    [Fact]
    public async Task A_fully_used_pack_has_nothing_to_refund()
    {
        var payment = await BuyPackAsync();
        await SpendAsync(1000, "all");

        var eligibility = await _refunds.GetEligibilityAsync(_tenant.Id, payment.Id);

        Assert.False(eligibility.Eligible);
    }

    [Fact]
    public async Task A_subscription_is_refundable_only_when_barely_used_and_the_refund_ends_the_plan()
    {
        var plan = new Plan { Code = "starter", Name = "Starter", PriceMonthlyCents = 3900 };
        _db.Plans.Add(plan);
        _db.PlanQuotas.Add(new PlanQuota { PlanId = plan.Id, QuotaType = QuotaType.AiConversations, IncludedUnits = 500 });
        var payment = new Payment
        {
            TenantId = _tenant.Id, Kind = PaymentKind.Subscription, PlanId = plan.Id, PlanName = "Starter",
            AmountCents = 3900, CurrencyCode = "INR", CurrencySymbol = "₹", LocalAmount = 3237m,
            Provider = "Simulated", PaidAtUtc = _clock.UtcNow.AddDays(-3)
        };
        _db.Payments.Add(payment);
        _db.Subscriptions.Add(new Subscription { TenantId = _tenant.Id, PlanId = plan.Id, Status = SubscriptionStatus.Active, CurrentPeriodStartUtc = payment.PaidAtUtc, CurrentPeriodEndUtc = payment.PaidAtUtc.AddMonths(1) });
        await _db.SaveChangesAsync();
        await _ledger.AllocatePlanQuotaAsync(_tenant.Id, plan.Id, payment.PaidAtUtc, payment.PaidAtUtc.AddMonths(1), payment.Id);

        await SpendAsync(100, "heavy");
        var heavy = await _refunds.GetEligibilityAsync(_tenant.Id, payment.Id);
        Assert.False(heavy.Eligible);
        Assert.Contains("20%", heavy.Reason);

        await _ledger.ReverseConsumptionAsync(_tenant.Id, "heavy");
        await SpendAsync(10, "light");
        var light = await _refunds.GetEligibilityAsync(_tenant.Id, payment.Id);
        Assert.True(light.Eligible);
        Assert.Equal(3510, light.AmountCents); // 27 of 30 days left, prorated

        var request = await _refunds.RequestAsync(_tenant.Id, payment.Id, "not for us", _tenantUser);
        await _refunds.ApproveAsync(request.Id, new ReviewRefundRequest("ok"), _admin);

        var subscription = await _db.Subscriptions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.Null(subscription.PlanId);
        Assert.Equal(0m, await BalanceAsync());
    }

    [Fact]
    public async Task Requests_nobody_answers_expire_and_return_the_units()
    {
        var payment = await BuyPackAsync();
        await _refunds.RequestAsync(_tenant.Id, payment.Id, "please", _tenantUser);
        Assert.Equal(0m, await BalanceAsync());

        _clock.UtcNow = _clock.UtcNow.AddDays(15);
        var closed = await _refunds.ExpireStaleAsync();

        Assert.Equal(1, closed);
        Assert.Equal(1000m, await BalanceAsync());
        Assert.Equal(RefundStatus.Expired, (await _db.RefundRequests.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Contains(_notifier.Sent, n => n.Kind == TenantNotificationKind.RefundExpired);
    }

    [Fact]
    public async Task An_operator_can_refund_outside_the_switch_and_the_window()
    {
        var old = await BuyPackAsync(paidAt: _clock.UtcNow.AddDays(-60));
        _tenant.RefundRequestsEnabled = false;
        await _db.SaveChangesAsync();

        var result = await _refunds.RefundDirectAsync(_tenant.Id, old.Id, "billing error on our side", _admin);

        Assert.Equal(RefundStatus.Refunded, result.Status);
        Assert.Equal(2000, result.RefundedAmountCents);
    }
}
