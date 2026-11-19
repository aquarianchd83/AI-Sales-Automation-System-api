using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The database's own guards for AutoPay - the ones that must hold even if the code above them is wrong or two workers race:
/// a gateway payment is recorded once, a tenant has one authorisation waiting, a charge maps to one Razorpay plan.</summary>
public sealed class RazorpayAutoPaySchemaTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SqliteApplicationDbContext _db;
    private readonly Tenant _tenant = new() { Name = "Acme", Slug = "acme" };
    private readonly Plan _plan = new() { Code = "premium", Name = "Premium" };
    private readonly RazorpayPlan _razorpayPlan;

    public RazorpayAutoPaySchemaTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _razorpayPlan = NewRazorpayPlan("plan_1", 284026);
        _db.Tenants.Add(_tenant);
        _db.Plans.Add(_plan);
        _db.RazorpayPlans.Add(_razorpayPlan);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task One_gateway_payment_can_be_recorded_only_once()
    {
        _db.Payments.Add(NewPayment("Razorpay", "pay_1"));
        await _db.SaveChangesAsync();

        _db.Payments.Add(NewPayment("Razorpay", "pay_1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Payments_without_a_gateway_id_are_not_limited()
    {
        _db.Payments.Add(NewPayment("Simulated", null));
        _db.Payments.Add(NewPayment("Simulated", null));
        _db.Payments.Add(NewPayment("Razorpay", "pay_1"));
        _db.Payments.Add(NewPayment("Razorpay", "pay_2"));

        await _db.SaveChangesAsync();

        Assert.Equal(4, await _db.Payments.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_payment_is_succeeded_unless_said_otherwise()
    {
        _db.Payments.Add(NewPayment("Simulated", null));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(PaymentStatus.Succeeded, (await _db.Payments.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task A_tenant_has_at_most_one_authorisation_waiting()
    {
        _db.RazorpaySubscriptions.Add(NewSubscription("sub_1", RazorpaySubscriptionStatus.Created));
        await _db.SaveChangesAsync();

        _db.RazorpaySubscriptions.Add(NewSubscription("sub_2", RazorpaySubscriptionStatus.Created));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Finished_and_running_mandates_do_not_block_a_new_authorisation()
    {
        _db.RazorpaySubscriptions.Add(NewSubscription("sub_1", RazorpaySubscriptionStatus.Cancelled));
        _db.RazorpaySubscriptions.Add(NewSubscription("sub_2", RazorpaySubscriptionStatus.Active));
        _db.RazorpaySubscriptions.Add(NewSubscription("sub_3", RazorpaySubscriptionStatus.Created));

        await _db.SaveChangesAsync();

        Assert.Equal(3, await _db.RazorpaySubscriptions.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_razorpay_subscription_id_is_unique()
    {
        _db.RazorpaySubscriptions.Add(NewSubscription("sub_1", RazorpaySubscriptionStatus.Cancelled));
        await _db.SaveChangesAsync();

        _db.RazorpaySubscriptions.Add(NewSubscription("sub_1", RazorpaySubscriptionStatus.Active));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task One_charge_maps_to_one_razorpay_plan()
    {
        _db.RazorpayPlans.Add(NewRazorpayPlan("plan_2", 284026));

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_new_price_or_mode_gets_its_own_razorpay_plan()
    {
        _db.RazorpayPlans.Add(NewRazorpayPlan("plan_2", 300000));
        var live = NewRazorpayPlan("plan_3", 284026);
        live.Mode = "live";
        _db.RazorpayPlans.Add(live);

        await _db.SaveChangesAsync();

        Assert.Equal(3, await _db.RazorpayPlans.CountAsync());
    }

    [Fact]
    public async Task A_stored_webhook_event_starts_as_received_and_keeps_its_whole_body()
    {
        var body = new string('x', 20_000);
        _db.RazorpayWebhookEvents.Add(new RazorpayWebhookEvent { EventId = "evt_1", Event = "subscription.charged", SubscriptionId = "sub_1", Payload = body });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var stored = await _db.RazorpayWebhookEvents.SingleAsync();
        Assert.Equal(RazorpayWebhookProcessingStatus.Received, stored.ProcessingStatus);
        Assert.Equal(body.Length, stored.Payload.Length);
        Assert.Equal(0, stored.RetryCount);
    }

    private RazorpayPlan NewRazorpayPlan(string razorpayPlanId, long amountMinor) => new()
    {
        PlanId = _plan.Id,
        Mode = "test",
        Currency = "INR",
        AmountMinor = amountMinor,
        RazorpayPlanId = razorpayPlanId
    };

    private RazorpaySubscription NewSubscription(string razorpaySubscriptionId, RazorpaySubscriptionStatus status) => new()
    {
        TenantId = _tenant.Id,
        PlanId = _plan.Id,
        RazorpayPlanRowId = _razorpayPlan.Id,
        RazorpaySubscriptionId = razorpaySubscriptionId,
        Status = status,
        AmountMinor = 284026,
        TotalCount = 120
    };

    private Payment NewPayment(string provider, string? providerPaymentId) => new()
    {
        TenantId = _tenant.Id,
        PlanId = _plan.Id,
        PlanName = "Premium",
        CurrencyCode = "INR",
        CurrencySymbol = "₹",
        LocalAmount = 2407m,
        TotalLocal = 2840.26m,
        Provider = provider,
        ProviderPaymentId = providerPaymentId,
        PaidAtUtc = DateTime.UtcNow
    };
}
