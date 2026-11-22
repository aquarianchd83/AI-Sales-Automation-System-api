using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsAppSalesAutomation.Application.Billing.AutoPay;
using WhatsAppSalesAutomation.Application.Common.Options;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Payments;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;
using WhatsAppSalesAutomation.Domain.Enums;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>What the payments side is asked to do, in order - and a way to make it fail, as a database or a wallet might.</summary>
public sealed class RecordingEntitlements : IAutoPayEntitlements
{
    public List<(string SubscriptionId, RazorpayWebhookPayment Payment)> Charges { get; } = new();
    public List<(string SubscriptionId, RazorpayWebhookPayment Payment)> Failures { get; } = new();
    public List<(string SubscriptionId, RazorpaySubscriptionStatus Previous, RazorpaySubscriptionStatus Current)> Changes { get; } = new();

    /// <summary>Thrown by the next charge, then cleared - "the wallet was unavailable just then".</summary>
    public Exception? FailNextCharge { get; set; }

    /// <summary>Thrown by every charge until cleared.</summary>
    public Exception? FailEveryCharge { get; set; }

    public Task ChargeSucceededAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default)
    {
        if (FailEveryCharge is not null)
            throw FailEveryCharge;
        if (FailNextCharge is { } once)
        {
            FailNextCharge = null;
            throw once;
        }

        Charges.Add((mandate.RazorpaySubscriptionId, payment));
        return Task.CompletedTask;
    }

    public Task ChargeFailedAsync(RazorpaySubscription mandate, RazorpayWebhookPayment payment, CancellationToken cancellationToken = default)
    {
        Failures.Add((mandate.RazorpaySubscriptionId, payment));
        return Task.CompletedTask;
    }

    public Task MandateChangedAsync(RazorpaySubscription mandate, RazorpaySubscriptionStatus previous, CancellationToken cancellationToken = default)
    {
        Changes.Add((mandate.RazorpaySubscriptionId, previous, mandate.Status));
        return Task.CompletedTask;
    }

    public List<string> Ended { get; } = new();

    public Task<bool> EndPaidAccessAsync(RazorpaySubscription mandate, CancellationToken cancellationToken = default)
    {
        Ended.Add(mandate.RazorpaySubscriptionId);
        return Task.FromResult(true);
    }
}

public sealed class RecordingWebhookQueue : IRazorpayWebhookQueue
{
    public List<Guid> Enqueued { get; } = new();
    public Exception? Fail { get; set; }

    public void Enqueue(Guid webhookEventRowId)
    {
        if (Fail is not null)
            throw Fail;
        Enqueued.Add(webhookEventRowId);
    }
}

/// <summary>
/// Razorpay's subscription webhooks from the wire to the mandate: stored whole before anything else, applied by the processor exactly once,
/// in whatever order they arrive, with failures kept and retried - and the daily comparison with Razorpay that catches what never arrived.
/// </summary>
public sealed class RazorpaySubscriptionWebhookTests : IDisposable
{
    private const string KeyId = "rzp_test_1DP5mmOlF5G5ag";
    private const string WebhookSecret = "webhook-secret-456";
    private const string Sub = "sub_T000001";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly SqliteApplicationDbContext _db;
    private readonly MemorySettingsStore _store = new();
    private readonly FakeSubscriptionGateway _gateway = new();
    private readonly RecordingEntitlements _entitlements = new();
    private readonly RecordingWebhookQueue _queue = new();
    private readonly AutoPayOptions _options = new();
    private readonly RazorpayService _receiver;
    private readonly RazorpaySubscriptionEventProcessor _processor;
    private readonly AutoPayMaintenanceService _maintenance;

    private readonly Tenant _tenant = new() { Name = "Mumbai Co", Slug = "mumbai", CountryCode = "IN", StateCode = "MH" };
    private readonly Plan _premium = new() { Code = "premium", Name = "Premium", IsActive = true };
    private readonly RazorpayPlan _razorpayPlan;
    private readonly long _t0;
    private int _eventNumber;

    public RazorpaySubscriptionWebhookTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        _db = new SqliteApplicationDbContext(options, new PlatformContext(), new AnonymousUser());
        _db.Database.EnsureCreated();

        _store.Rows[RazorpayKeyStore.LegacyKeyIdKey] = KeyId;
        _store.Rows[RazorpayKeyStore.LegacyKeySecretKey] = "key-secret-123";
        _store.Rows[RazorpayKeyStore.LegacyWebhookSecretKey] = WebhookSecret;

        _receiver = new RazorpayService(_db, _store, _gateway, _clock, NullLogger<RazorpayService>.Instance, _queue);
        _processor = new RazorpaySubscriptionEventProcessor(_db, _store, _gateway, _entitlements, _clock, NullLogger<RazorpaySubscriptionEventProcessor>.Instance);
        _maintenance = new AutoPayMaintenanceService(
            _db, _store, _gateway, _processor, _entitlements, _clock, new FixedOptions<AutoPayOptions>(_options), NullLogger<AutoPayMaintenanceService>.Instance);

        _razorpayPlan = new RazorpayPlan { PlanId = _premium.Id, Mode = "test", AmountMinor = 284026, RazorpayPlanId = "plan_T000001" };
        _db.Tenants.Add(_tenant);
        _db.Plans.Add(_premium);
        _db.RazorpayPlans.Add(_razorpayPlan);
        _db.SaveChanges();

        _t0 = new DateTimeOffset(_clock.UtcNow, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Building blocks ─────────────────────────────────────────────────────────────────

    /// <summary>A mandate as the checkout leaves it: created an hour ago, waiting to be authorised, and known to Razorpay.</summary>
    private RazorpaySubscription Mandate(string id = Sub, RazorpaySubscriptionStatus status = RazorpaySubscriptionStatus.Created, string mode = "test", double createdHoursAgo = 1)
    {
        var row = new RazorpaySubscription
        {
            TenantId = _tenant.Id, PlanId = _premium.Id, RazorpayPlanRowId = _razorpayPlan.Id, RazorpaySubscriptionId = id, Mode = mode,
            Status = status, AmountMinor = 284026, TotalCount = 120, ShortUrl = $"https://rzp.io/i/{id}", CreatedAt = _clock.UtcNow.AddHours(-createdHoursAgo)
        };
        _db.RazorpaySubscriptions.Add(row);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        _gateway.Subscriptions[id] = new RazorpaySubscriptionInfo(
            id, "plan_T000001", status.ToString().ToLowerInvariant(), row.ShortUrl, 120, 0, 120, null, null, null, null, null, null, null, null);
        return row;
    }

    private static object Payment(string id = "pay_1", string status = "captured", long amount = 284026, string? error = null) => new
    {
        entity = new { id, entity = "payment", amount, currency = "INR", status, method = "upi", invoice_id = "inv_1", order_id = "order_1", error_description = error, created_at = 1_757_505_700 }
    };

    /// <summary>A subscription webhook body, shaped like Razorpay's. <paramref name="at"/> is seconds after the clock's "now".</summary>
    private string Body(string name, int at, string status, string id = Sub, int paid = 0, object? payment = null, bool ours = true, string? padding = null)
    {
        var start = _t0;
        var subscription = new
        {
            entity = new
            {
                id, entity = "subscription", plan_id = "plan_T000001", status,
                current_start = paid > 0 ? start : (long?)null,
                current_end = paid > 0 ? start + 2_592_000 : (long?)null,
                charge_at = status is "active" or "authenticated" ? start + 2_592_000 : (long?)null,
                ended_at = status == "cancelled" ? _t0 + at : (long?)null,
                total_count = 120, paid_count = paid, remaining_count = 120 - paid,
                short_url = $"https://rzp.io/i/{id}", customer_id = "cust_1", payment_method = "upi",
                notes = ours ? new Dictionary<string, string> { ["source"] = "autopay", ["padding"] = padding ?? string.Empty } : new Dictionary<string, string>()
            }
        };

        return JsonSerializer.Serialize(new
        {
            entity = "event",
            account_id = "acc_1",
            @event = name,
            contains = payment is null ? new[] { "subscription" } : new[] { "subscription", "payment" },
            payload = payment is null ? (object)new { subscription } : new { subscription, payment },
            created_at = _t0 + at
        });
    }

    /// <summary>Stores an event the way the receiver does, without the HTTP side, and returns its row id.</summary>
    private Guid Stored(string body, string? eventId = null, double receivedMinutesAgo = 0)
    {
        var hook = RazorpaySubscriptionWebhook.Parse(body)!;
        var record = new RazorpayWebhookEvent
        {
            EventId = eventId ?? $"evt_{++_eventNumber}", Event = hook.Event, SubscriptionId = hook.Subscription?.Id, Payload = body,
            EventCreatedAtUtc = hook.CreatedAtUtc, CreatedAt = _clock.UtcNow.AddMinutes(-receivedMinutesAgo)
        };
        _db.RazorpayWebhookEvents.Add(record);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return record.Id;
    }

    private async Task<RazorpayEventOutcome> ProcessAsync(string body, string? eventId = null)
    {
        var outcome = await _processor.ProcessAsync(Stored(body, eventId));
        _db.ChangeTracker.Clear();
        return outcome;
    }

    private Task<RazorpaySubscription> MandateNowAsync(string id = Sub) =>
        _db.RazorpaySubscriptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.RazorpaySubscriptionId == id);

    private Task<RazorpayWebhookEvent> EventAsync(Guid id) => _db.RazorpayWebhookEvents.AsNoTracking().SingleAsync(e => e.Id == id);

    private static string Sign(string body) => RazorpaySignatures.Compute(WebhookSecret, Encoding.UTF8.GetBytes(body));

    private Task<RazorpayWebhookOutcome> ReceiveAsync(string body, string eventId, string? signature = null) =>
        _receiver.HandleWebhookAsync(Encoding.UTF8.GetBytes(body), signature ?? Sign(body), eventId);

    // ── Receiving: stored first, applied later ──────────────────────────────────────────

    [Fact]
    public async Task A_subscription_event_is_stored_whole_and_queued_not_applied_in_the_request()
    {
        Mandate();
        // Far more than the 8,000 characters the table used to keep.
        var body = Body("subscription.authenticated", 10, "authenticated", padding: new string('x', 20_000));

        var outcome = await ReceiveAsync(body, "evt_A");

        Assert.Equal(RazorpayWebhookOutcome.Queued, outcome);
        var stored = await _db.RazorpayWebhookEvents.AsNoTracking().SingleAsync();
        Assert.Equal(body, stored.Payload);
        Assert.Equal(("evt_A", "subscription.authenticated", Sub), (stored.EventId, stored.Event, stored.SubscriptionId));
        Assert.Equal(RazorpayWebhookProcessingStatus.Received, stored.ProcessingStatus);
        Assert.Equal(_clock.UtcNow.AddSeconds(10), stored.EventCreatedAtUtc);
        Assert.False(stored.Applied);
        Assert.Equal(stored.Id, Assert.Single(_queue.Enqueued));

        // Nothing has touched the mandate yet: that is the job's work.
        Assert.Equal(RazorpaySubscriptionStatus.Created, (await MandateNowAsync()).Status);
        Assert.Empty(_entitlements.Changes);
    }

    [Fact]
    public async Task The_same_event_delivered_five_times_is_stored_and_queued_once()
    {
        Mandate();
        var body = Body("subscription.charged", 10, "active", paid: 1, payment: Payment());

        var outcomes = new List<RazorpayWebhookOutcome>();
        for (var i = 0; i < 5; i++)
            outcomes.Add(await ReceiveAsync(body, "evt_A"));

        Assert.Equal(RazorpayWebhookOutcome.Queued, outcomes[0]);
        Assert.All(outcomes.Skip(1), o => Assert.Equal(RazorpayWebhookOutcome.Duplicate, o));
        Assert.Equal(1, await _db.RazorpayWebhookEvents.CountAsync());
        Assert.Single(_queue.Enqueued);
    }

    [Fact]
    public async Task An_event_with_a_bad_signature_is_neither_stored_nor_queued()
    {
        Mandate();
        var body = Body("subscription.charged", 10, "active", paid: 1, payment: Payment());

        var outcome = await ReceiveAsync(body, "evt_A", signature: Sign(body + " "));

        Assert.Equal(RazorpayWebhookOutcome.InvalidSignature, outcome);
        Assert.False(await _db.RazorpayWebhookEvents.AnyAsync());
        Assert.Empty(_queue.Enqueued);
    }

    [Fact]
    public async Task If_the_queue_is_down_the_event_is_still_stored_and_acknowledged()
    {
        Mandate();
        _queue.Fail = new InvalidOperationException("Hangfire storage unavailable");

        var outcome = await ReceiveAsync(Body("subscription.authenticated", 10, "authenticated"), "evt_A");

        Assert.Equal(RazorpayWebhookOutcome.Queued, outcome);
        Assert.Equal(RazorpayWebhookProcessingStatus.Received, (await _db.RazorpayWebhookEvents.AsNoTracking().SingleAsync()).ProcessingStatus);
    }

    [Fact]
    public async Task What_was_stored_through_the_endpoint_is_what_the_processor_applies()
    {
        Mandate();
        await ReceiveAsync(Body("subscription.authenticated", 10, "authenticated"), "evt_A");
        _db.ChangeTracker.Clear();

        var outcome = await _processor.ProcessAsync(_queue.Enqueued.Single());

        Assert.Equal(RazorpayEventOutcome.Completed, outcome);
        Assert.Equal(RazorpaySubscriptionStatus.Authenticated, (await MandateNowAsync()).Status);
    }

    [Fact]
    public async Task Events_for_the_test_pages_orders_are_still_applied_in_the_request()
    {
        var body = "{\"event\":\"payment.captured\",\"created_at\":1757505600,\"payload\":{\"payment\":{\"entity\":{\"id\":\"pay_9\",\"order_id\":\"order_unknown\",\"status\":\"captured\"}}}}";

        var outcome = await ReceiveAsync(body, "evt_O");

        Assert.Equal(RazorpayWebhookOutcome.Ignored, outcome);
        Assert.Equal(RazorpayWebhookProcessingStatus.Ignored, (await _db.RazorpayWebhookEvents.AsNoTracking().SingleAsync()).ProcessingStatus);
        Assert.Empty(_queue.Enqueued);
    }

    // ── Processing: the mandate follows Razorpay ────────────────────────────────────────

    [Fact]
    public async Task Authenticated_moves_the_mandate_and_completes_the_event()
    {
        Mandate();
        var id = Stored(Body("subscription.authenticated", 10, "authenticated"));

        var outcome = await _processor.ProcessAsync(id);

        Assert.Equal(RazorpayEventOutcome.Completed, outcome);
        var mandate = await MandateNowAsync();
        Assert.Equal(RazorpaySubscriptionStatus.Authenticated, mandate.Status);
        Assert.Equal(_clock.UtcNow, mandate.AuthenticatedAtUtc);
        Assert.Equal(("cust_1", "upi"), (mandate.CustomerId, mandate.PaymentMethod));
        Assert.Equal(_clock.UtcNow.AddSeconds(10), mandate.LastEventAtUtc);
        Assert.Equal("subscription.authenticated", mandate.LastEvent);

        var record = await EventAsync(id);
        Assert.Equal(RazorpayWebhookProcessingStatus.Completed, record.ProcessingStatus);
        Assert.True(record.Applied);
        Assert.Equal(_clock.UtcNow, record.ProcessedAtUtc);
        Assert.Null(record.ErrorMessage);

        Assert.Equal((Sub, RazorpaySubscriptionStatus.Created, RazorpaySubscriptionStatus.Authenticated), Assert.Single(_entitlements.Changes));
    }

    [Fact]
    public async Task Charged_records_the_billing_period_and_passes_the_payment_on_once()
    {
        Mandate();

        var outcome = await ProcessAsync(Body("subscription.charged", 20, "active", paid: 1, payment: Payment()));

        Assert.Equal(RazorpayEventOutcome.Completed, outcome);
        var mandate = await MandateNowAsync();
        Assert.Equal(RazorpaySubscriptionStatus.Active, mandate.Status);
        Assert.Equal(1, mandate.PaidCount);
        Assert.Equal(_clock.UtcNow, mandate.CurrentStartUtc);
        Assert.Equal(_clock.UtcNow.AddDays(30), mandate.CurrentEndUtc);
        Assert.Equal(_clock.UtcNow.AddDays(30), mandate.ChargeAtUtc);

        var charge = Assert.Single(_entitlements.Charges);
        Assert.Equal(Sub, charge.SubscriptionId);
        Assert.Equal(("pay_1", 284026L, "INR", "captured", "upi", "inv_1"),
            (charge.Payment.Id, charge.Payment.Amount, charge.Payment.Currency, charge.Payment.Status, charge.Payment.Method, charge.Payment.InvoiceId));
        Assert.Empty(_entitlements.Failures);
    }

    [Fact]
    public async Task Processing_the_same_stored_event_again_does_nothing()
    {
        Mandate();
        var id = Stored(Body("subscription.charged", 20, "active", paid: 1, payment: Payment()));

        Assert.Equal(RazorpayEventOutcome.Completed, await _processor.ProcessAsync(id));
        for (var i = 0; i < 4; i++)
            Assert.Equal(RazorpayEventOutcome.AlreadyDone, await _processor.ProcessAsync(id));

        Assert.Single(_entitlements.Charges);
        Assert.Single(_entitlements.Changes);
    }

    [Fact]
    public async Task Pending_with_a_failed_payment_reports_the_failure_and_grants_nothing()
    {
        Mandate(status: RazorpaySubscriptionStatus.Active);

        await ProcessAsync(Body("subscription.pending", 20, "pending", paid: 1, payment: Payment("pay_2", "failed", error: "Insufficient balance")));

        Assert.Equal(RazorpaySubscriptionStatus.Pending, (await MandateNowAsync()).Status);
        var failure = Assert.Single(_entitlements.Failures);
        Assert.Equal(("pay_2", "failed", "Insufficient balance"), (failure.Payment.Id, failure.Payment.Status, failure.Payment.ErrorDescription));
        Assert.Empty(_entitlements.Charges);
        Assert.Equal((Sub, RazorpaySubscriptionStatus.Active, RazorpaySubscriptionStatus.Pending), Assert.Single(_entitlements.Changes));
    }

    [Theory]
    [InlineData("subscription.halted", "halted", RazorpaySubscriptionStatus.Halted)]
    [InlineData("subscription.paused", "paused", RazorpaySubscriptionStatus.Paused)]
    [InlineData("subscription.cancelled", "cancelled", RazorpaySubscriptionStatus.Cancelled)]
    [InlineData("subscription.completed", "completed", RazorpaySubscriptionStatus.Completed)]
    public async Task Each_lifecycle_event_moves_the_mandate_to_razorpays_state(string name, string razorpayStatus, RazorpaySubscriptionStatus expected)
    {
        Mandate(status: RazorpaySubscriptionStatus.Active);

        await ProcessAsync(Body(name, 20, razorpayStatus, paid: 1));

        var mandate = await MandateNowAsync();
        Assert.Equal(expected, mandate.Status);
        Assert.Equal((Sub, RazorpaySubscriptionStatus.Active, expected), Assert.Single(_entitlements.Changes));
        Assert.Empty(_entitlements.Charges);
        if (RazorpayMandateState.IsEnded(expected))
        {
            Assert.NotNull(mandate.EndedAtUtc);
            Assert.Null(mandate.ChargeAtUtc);
        }
    }

    [Fact]
    public async Task Resumed_carries_razorpays_status_which_is_active_not_resumed()
    {
        Mandate(status: RazorpaySubscriptionStatus.Paused);

        await ProcessAsync(Body("subscription.resumed", 20, "active", paid: 1));

        Assert.Equal(RazorpaySubscriptionStatus.Active, (await MandateNowAsync()).Status);
    }

    [Fact]
    public async Task An_event_that_changes_nothing_reports_no_change()
    {
        Mandate(status: RazorpaySubscriptionStatus.Active);

        await ProcessAsync(Body("subscription.updated", 20, "active", paid: 1));

        Assert.Empty(_entitlements.Changes);
    }

    // ── Processing: not ours, or not yet ────────────────────────────────────────────────

    [Fact]
    public async Task A_subscription_this_platform_did_not_create_is_ignored()
    {
        var id = Stored(Body("subscription.charged", 20, "active", id: "sub_FOREIGN", paid: 1, payment: Payment(), ours: false));

        var outcome = await _processor.ProcessAsync(id);

        Assert.Equal(RazorpayEventOutcome.Ignored, outcome);
        var record = await EventAsync(id);
        Assert.Equal(RazorpayWebhookProcessingStatus.Ignored, record.ProcessingStatus);
        Assert.False(record.Applied);
        Assert.Empty(_entitlements.Charges);
    }

    [Fact]
    public async Task One_of_ours_with_no_mandate_record_is_kept_and_retried_not_dropped()
    {
        // The event overtook the checkout's own save.
        var id = Stored(Body("subscription.authenticated", 10, "authenticated"));

        Assert.Equal(RazorpayEventOutcome.Failed, await _processor.ProcessAsync(id));
        var record = await EventAsync(id);
        Assert.Equal((RazorpayWebhookProcessingStatus.Failed, 1), (record.ProcessingStatus, record.RetryCount));
        Assert.Contains("no mandate record", record.ErrorMessage);

        // The checkout finishes; the next try applies it.
        Mandate();
        Assert.Equal(RazorpayEventOutcome.Completed, await _processor.ProcessAsync(id));
        Assert.Equal(RazorpaySubscriptionStatus.Authenticated, (await MandateNowAsync()).Status);
    }

    [Fact]
    public async Task A_stored_body_that_is_not_json_is_ignored_not_retried_forever()
    {
        _db.RazorpayWebhookEvents.Add(new RazorpayWebhookEvent { EventId = "evt_bad", Event = "subscription.charged", Payload = "not json" });
        await _db.SaveChangesAsync();
        var id = (await _db.RazorpayWebhookEvents.AsNoTracking().SingleAsync()).Id;

        Assert.Equal(RazorpayEventOutcome.Ignored, await _processor.ProcessAsync(id));
    }

    [Fact]
    public async Task An_unknown_row_id_is_reported_not_thrown()
    {
        Assert.Equal(RazorpayEventOutcome.NotFound, await _processor.ProcessAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_charge_with_no_payment_is_never_passed_as_handled()
    {
        Mandate();
        var id = Stored(Body("subscription.charged", 20, "active", paid: 1));

        Assert.Equal(RazorpayEventOutcome.Failed, await _processor.ProcessAsync(id));
        Assert.Equal(RazorpayWebhookProcessingStatus.Failed, (await EventAsync(id)).ProcessingStatus);
        Assert.Empty(_entitlements.Charges);
    }

    // ── Processing: any order ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Events_in_order_are_taken_as_they_come_without_asking_razorpay()
    {
        Mandate();

        await ProcessAsync(Body("subscription.authenticated", 10, "authenticated"));
        await ProcessAsync(Body("subscription.activated", 11, "active"));
        await ProcessAsync(Body("subscription.charged", 12, "active", paid: 1, payment: Payment()));

        Assert.Empty(_gateway.FetchCalls);
        var mandate = await MandateNowAsync();
        Assert.Equal((RazorpaySubscriptionStatus.Active, 1), (mandate.Status, mandate.PaidCount));
        Assert.Single(_entitlements.Charges);
    }

    [Fact]
    public async Task An_older_event_arriving_late_cannot_undo_a_newer_one()
    {
        Mandate();
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { Status = "active", PaidCount = 1 };

        // Razorpay raised authenticated (t+10) then charged (t+12); they arrive the other way round.
        await ProcessAsync(Body("subscription.charged", 12, "active", paid: 1, payment: Payment()));
        var late = Stored(Body("subscription.authenticated", 10, "authenticated"));
        Assert.Equal(RazorpayEventOutcome.Completed, await _processor.ProcessAsync(late));

        var mandate = await MandateNowAsync();
        Assert.Equal(RazorpaySubscriptionStatus.Active, mandate.Status);
        Assert.Equal(1, mandate.PaidCount);
        // The late one was not believed: Razorpay was asked where the subscription stands.
        Assert.Equal(Sub, Assert.Single(_gateway.FetchCalls));
        Assert.Contains("out of order", (await EventAsync(late)).Note);
        Assert.Equal(_clock.UtcNow.AddSeconds(12), mandate.LastEventAtUtc);
        Assert.Single(_entitlements.Charges);
    }

    [Fact]
    public async Task Events_raised_in_the_same_second_are_settled_by_asking_razorpay()
    {
        Mandate();
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { Status = "active", PaidCount = 1 };

        // activated and authenticated share a second and arrive "backwards": which came last cannot be told from the events.
        await ProcessAsync(Body("subscription.activated", 10, "active"));
        await ProcessAsync(Body("subscription.authenticated", 10, "authenticated"));

        Assert.Equal(RazorpaySubscriptionStatus.Active, (await MandateNowAsync()).Status);
        Assert.Single(_gateway.FetchCalls);
    }

    [Fact]
    public async Task If_razorpay_cannot_settle_an_out_of_order_event_it_is_retried_not_guessed()
    {
        Mandate();
        await ProcessAsync(Body("subscription.charged", 12, "active", paid: 1, payment: Payment()));
        _gateway.FetchError = "Could not reach Razorpay";
        var late = Stored(Body("subscription.authenticated", 10, "authenticated"));

        Assert.Equal(RazorpayEventOutcome.Failed, await _processor.ProcessAsync(late));

        Assert.Equal(RazorpaySubscriptionStatus.Active, (await MandateNowAsync()).Status);
        Assert.Equal(RazorpayWebhookProcessingStatus.Failed, (await EventAsync(late)).ProcessingStatus);
    }

    [Fact]
    public async Task A_payment_that_arrives_before_its_authorisation_event_is_still_recorded()
    {
        Mandate();

        // charged first, with nothing before it.
        await ProcessAsync(Body("subscription.charged", 12, "active", paid: 1, payment: Payment()));

        Assert.Single(_entitlements.Charges);
        Assert.Equal(RazorpaySubscriptionStatus.Active, (await MandateNowAsync()).Status);
    }

    [Fact]
    public async Task A_mandate_that_has_ended_is_never_brought_back_by_a_late_event()
    {
        Mandate(status: RazorpaySubscriptionStatus.Active);
        await ProcessAsync(Body("subscription.cancelled", 30, "cancelled", paid: 1));

        // Newer by its timestamp, even - but cancelled is final.
        await ProcessAsync(Body("subscription.activated", 40, "active", paid: 1));

        var mandate = await MandateNowAsync();
        Assert.Equal(RazorpaySubscriptionStatus.Cancelled, mandate.Status);
        Assert.Empty(_gateway.FetchCalls);
        Assert.Single(_entitlements.Changes);
    }

    [Fact]
    public async Task A_charge_that_turns_up_after_the_cancellation_is_still_passed_on()
    {
        Mandate(status: RazorpaySubscriptionStatus.Active);
        await ProcessAsync(Body("subscription.cancelled", 30, "cancelled", paid: 1));

        // The debit happened at t+20, before the cancellation; its event is simply late. The money was taken, so it is owed its record.
        await ProcessAsync(Body("subscription.charged", 20, "active", paid: 1, payment: Payment()));

        Assert.Equal("pay_1", Assert.Single(_entitlements.Charges).Payment.Id);
        Assert.Equal(RazorpaySubscriptionStatus.Cancelled, (await MandateNowAsync()).Status);
    }

    // ── Processing: when applying fails ─────────────────────────────────────────────────

    [Fact]
    public async Task When_recording_the_payment_fails_the_event_is_kept_with_the_error()
    {
        Mandate();
        _entitlements.FailNextCharge = new InvalidOperationException("The wallet could not be reached.");
        var id = Stored(Body("subscription.charged", 20, "active", paid: 1, payment: Payment()));

        var outcome = await _processor.ProcessAsync(id);

        Assert.Equal(RazorpayEventOutcome.Failed, outcome);
        var record = await EventAsync(id);
        Assert.Equal(RazorpayWebhookProcessingStatus.Failed, record.ProcessingStatus);
        Assert.Equal(1, record.RetryCount);
        Assert.Equal("The wallet could not be reached.", record.ErrorMessage);
        Assert.Equal(_clock.UtcNow, record.ProcessedAtUtc);
        Assert.False(record.Applied);
        Assert.Empty(_entitlements.Charges);
    }

    [Fact]
    public async Task A_payment_that_succeeded_but_could_not_be_recorded_is_recorded_on_the_retry()
    {
        Mandate();
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { Status = "active", PaidCount = 1 };
        _entitlements.FailNextCharge = new InvalidOperationException("The wallet could not be reached.");
        var id = Stored(Body("subscription.charged", 20, "active", paid: 1, payment: Payment()));
        await _processor.ProcessAsync(id);

        var outcome = await _processor.ProcessAsync(id);

        Assert.Equal(RazorpayEventOutcome.Completed, outcome);
        Assert.Equal("pay_1", Assert.Single(_entitlements.Charges).Payment.Id);
        var record = await EventAsync(id);
        Assert.Equal(RazorpayWebhookProcessingStatus.Completed, record.ProcessingStatus);
        Assert.Null(record.ErrorMessage);
        // The count of failures is kept as history.
        Assert.Equal(1, record.RetryCount);
        Assert.Equal(RazorpaySubscriptionStatus.Active, (await MandateNowAsync()).Status);
    }

    [Fact]
    public async Task A_long_error_is_cut_to_fit_not_allowed_to_fail_the_save()
    {
        Mandate();
        _entitlements.FailNextCharge = new InvalidOperationException(new string('e', 5000));
        var id = Stored(Body("subscription.charged", 20, "active", paid: 1, payment: Payment()));

        await _processor.ProcessAsync(id);

        Assert.Equal(1000, (await EventAsync(id)).ErrorMessage!.Length);
    }

    // ── The retry pass ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_retry_pass_applies_an_event_no_job_picked_up()
    {
        Mandate();
        var stale = Stored(Body("subscription.authenticated", 10, "authenticated"), receivedMinutesAgo: 3);

        var tried = await _maintenance.RetryWebhooksAsync();

        Assert.Equal(1, tried);
        Assert.Equal(RazorpayWebhookProcessingStatus.Completed, (await EventAsync(stale)).ProcessingStatus);
        Assert.Equal(RazorpaySubscriptionStatus.Authenticated, (await MandateNowAsync()).Status);
    }

    [Fact]
    public async Task The_retry_pass_leaves_a_fresh_event_to_its_own_job()
    {
        Mandate();
        var fresh = Stored(Body("subscription.authenticated", 10, "authenticated"), receivedMinutesAgo: 1);

        Assert.Equal(0, await _maintenance.RetryWebhooksAsync());
        Assert.Equal(RazorpayWebhookProcessingStatus.Received, (await EventAsync(fresh)).ProcessingStatus);
    }

    [Fact]
    public async Task A_failed_event_is_retried_only_after_its_wait_and_then_succeeds()
    {
        Mandate();
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { Status = "active", PaidCount = 1 };
        _entitlements.FailNextCharge = new InvalidOperationException("The wallet could not be reached.");
        var id = Stored(Body("subscription.charged", 20, "active", paid: 1, payment: Payment()), receivedMinutesAgo: 10);
        await _processor.ProcessAsync(id);
        _db.ChangeTracker.Clear();

        // One failure: five minutes' wait.
        _clock.UtcNow = _clock.UtcNow.AddMinutes(4);
        Assert.Equal(0, await _maintenance.RetryWebhooksAsync());
        Assert.Empty(_entitlements.Charges);

        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
        Assert.Equal(1, await _maintenance.RetryWebhooksAsync());
        Assert.Single(_entitlements.Charges);
        Assert.Equal(RazorpayWebhookProcessingStatus.Completed, (await EventAsync(id)).ProcessingStatus);

        // And once done it is never tried again.
        _clock.UtcNow = _clock.UtcNow.AddDays(1);
        Assert.Equal(0, await _maintenance.RetryWebhooksAsync());
        Assert.Single(_entitlements.Charges);
    }

    [Fact]
    public async Task An_event_that_keeps_failing_is_given_up_on_and_left_for_a_person()
    {
        Mandate();
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { Status = "active", PaidCount = 1 };
        _options.WebhookMaxAttempts = 3;
        _entitlements.FailEveryCharge = new InvalidOperationException("Still broken.");
        var id = Stored(Body("subscription.charged", 20, "active", paid: 1, payment: Payment()), receivedMinutesAgo: 10);

        var attempts = 0;
        for (var i = 0; i < 10; i++)
        {
            attempts += await _maintenance.RetryWebhooksAsync();
            _clock.UtcNow = _clock.UtcNow.AddHours(7);
        }

        Assert.Equal(3, attempts);
        var record = await EventAsync(id);
        // Never quietly marked done, never deleted: it stays Failed with the reason.
        Assert.Equal((RazorpayWebhookProcessingStatus.Failed, 3, "Still broken."), (record.ProcessingStatus, record.RetryCount, record.ErrorMessage));
        Assert.Empty(_entitlements.Charges);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(6, 160)]
    [InlineData(7, 320)]
    [InlineData(8, 360)]
    [InlineData(50, 360)]
    public void The_wait_between_tries_doubles_up_to_six_hours(int failures, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), AutoPayMaintenanceService.BackoffAfter(failures));
    }

    // ── Reconciliation ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconciliation_repairs_a_mandate_whose_webhooks_never_arrived()
    {
        Mandate();
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with
        {
            Status = "active", PaidCount = 0, CurrentStart = _t0, CurrentEnd = _t0 + 2_592_000, ChargeAt = _t0 + 2_592_000, CustomerId = "cust_1"
        };

        var result = await _maintenance.ReconcileAsync();

        Assert.Equal(new AutoPayReconciliationResult(1, 1, 0, 0, 0), result);
        var mandate = await MandateNowAsync();
        Assert.Equal(RazorpaySubscriptionStatus.Active, mandate.Status);
        Assert.Equal(_clock.UtcNow.AddDays(30), mandate.CurrentEndUtc);
        Assert.Equal(_clock.UtcNow, mandate.LastReconciledAtUtc);
        Assert.Equal((Sub, RazorpaySubscriptionStatus.Created, RazorpaySubscriptionStatus.Active), Assert.Single(_entitlements.Changes));
    }

    [Fact]
    public async Task Reconciliation_changes_nothing_when_both_sides_agree()
    {
        Mandate(status: RazorpaySubscriptionStatus.Active);

        var result = await _maintenance.ReconcileAsync();

        Assert.Equal(new AutoPayReconciliationResult(1, 0, 0, 0, 0), result);
        Assert.Empty(_entitlements.Changes);
        Assert.Empty(_gateway.CancelCalls);
    }

    [Fact]
    public async Task When_razorpays_invoices_do_not_account_for_a_missing_payment_the_gap_is_reported()
    {
        Mandate(status: RazorpaySubscriptionStatus.Active);
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { PaidCount = 2 };
        _db.Payments.Add(new Payment
        {
            TenantId = _tenant.Id, PlanName = "Premium", CurrencyCode = "INR", CurrencySymbol = "₹", Provider = "Razorpay",
            ProviderPaymentId = "pay_1", ProviderSubscriptionId = Sub, PaidAtUtc = _clock.UtcNow
        });
        _db.SaveChanges();

        var result = await _maintenance.ReconcileAsync();

        Assert.Equal(1, result.PaymentsMissing);
        // Nothing new is invented: the only payment handed on is the one already on record, re-applied so the tenant's plan rests on it.
        Assert.All(_entitlements.Charges, c => Assert.Equal("pay_1", c.Item2.Id));
        Assert.Equal(1, await _db.Payments.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task A_failed_payment_row_does_not_count_as_a_payment_received()
    {
        Mandate(status: RazorpaySubscriptionStatus.Active);
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { PaidCount = 1 };
        _db.Payments.Add(new Payment
        {
            TenantId = _tenant.Id, PlanName = "Premium", CurrencyCode = "INR", CurrencySymbol = "₹", Provider = "Razorpay",
            ProviderPaymentId = "pay_f", ProviderSubscriptionId = Sub, Status = PaymentStatus.Failed, PaidAtUtc = _clock.UtcNow
        });
        _db.SaveChanges();

        Assert.Equal(1, (await _maintenance.ReconcileAsync()).PaymentsMissing);
    }

    [Fact]
    public async Task An_authorisation_nobody_completed_is_cancelled_at_razorpay()
    {
        Mandate(createdHoursAgo: 80);

        var result = await _maintenance.ReconcileAsync();

        Assert.Equal(new AutoPayReconciliationResult(1, 0, 1, 0, 0), result);
        Assert.Equal((Sub, false), Assert.Single(_gateway.CancelCalls));
        var mandate = await MandateNowAsync();
        Assert.Equal(RazorpaySubscriptionStatus.Cancelled, mandate.Status);
        Assert.NotNull(mandate.EndedAtUtc);
    }

    [Fact]
    public async Task A_recent_authorisation_is_left_waiting()
    {
        Mandate(createdHoursAgo: 10);

        await _maintenance.ReconcileAsync();

        Assert.Empty(_gateway.CancelCalls);
        Assert.Equal(RazorpaySubscriptionStatus.Created, (await MandateNowAsync()).Status);
    }

    [Fact]
    public async Task An_old_authorisation_razorpay_already_expired_needs_no_cancelling()
    {
        Mandate(createdHoursAgo: 80);
        _gateway.Subscriptions[Sub] = _gateway.Subscriptions[Sub] with { Status = "expired" };

        var result = await _maintenance.ReconcileAsync();

        Assert.Empty(_gateway.CancelCalls);
        Assert.Equal(0, result.AbandonedCancelled);
        Assert.Equal(RazorpaySubscriptionStatus.Expired, (await MandateNowAsync()).Status);
    }

    [Fact]
    public async Task One_mandate_that_cannot_be_checked_does_not_stop_the_others()
    {
        Mandate("sub_MISSING", RazorpaySubscriptionStatus.Active);
        _gateway.Subscriptions.Remove("sub_MISSING");
        Mandate("sub_OK");
        _gateway.Subscriptions["sub_OK"] = _gateway.Subscriptions["sub_OK"] with { Status = "authenticated" };

        var result = await _maintenance.ReconcileAsync();

        Assert.Equal(new AutoPayReconciliationResult(1, 1, 0, 0, 1), result);
        Assert.Equal(RazorpaySubscriptionStatus.Authenticated, (await MandateNowAsync("sub_OK")).Status);
        Assert.Equal(RazorpaySubscriptionStatus.Active, (await MandateNowAsync("sub_MISSING")).Status);
    }

    [Fact]
    public async Task Finished_mandates_and_ones_made_with_other_keys_are_not_asked_about()
    {
        Mandate("sub_DONE", RazorpaySubscriptionStatus.Cancelled);
        Mandate("sub_LIVE", RazorpaySubscriptionStatus.Active, mode: "live");

        var result = await _maintenance.ReconcileAsync();

        Assert.Equal(0, result.Checked);
        Assert.Empty(_gateway.FetchCalls);
    }

    [Fact]
    public async Task Without_razorpay_keys_reconciliation_does_nothing()
    {
        Mandate();
        _store.Rows.Remove(RazorpayKeyStore.LegacyKeySecretKey);

        Assert.Equal(new AutoPayReconciliationResult(0, 0, 0, 0, 0), await _maintenance.ReconcileAsync());
        Assert.Empty(_gateway.FetchCalls);
    }

    // ── Reading the body ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_subscription_event_is_read_with_its_subscription_and_payment()
    {
        var hook = RazorpaySubscriptionWebhook.Parse(Body("subscription.charged", 20, "active", paid: 1, payment: Payment()))!;

        Assert.Equal("subscription.charged", hook.Event);
        Assert.Equal(_clock.UtcNow.AddSeconds(20), hook.CreatedAtUtc);
        Assert.True(hook.CreatedByThisPlatform);
        Assert.Equal((Sub, "active", 1, 119, "cust_1"), (hook.Subscription!.Id, hook.Subscription.Status, hook.Subscription.PaidCount, hook.Subscription.RemainingCount, hook.Subscription.CustomerId));
        Assert.Equal(_t0, hook.Subscription.CurrentStart);
        Assert.Equal(("pay_1", 284026L, "captured", "inv_1"), (hook.Payment!.Id, hook.Payment.Amount, hook.Payment.Status, hook.Payment.InvoiceId));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void A_body_that_is_not_an_event_reads_as_nothing(string body)
    {
        Assert.Null(RazorpaySubscriptionWebhook.Parse(body));
    }

    [Fact]
    public void An_event_with_no_subscription_or_payment_reads_without_them()
    {
        var hook = RazorpaySubscriptionWebhook.Parse("{\"event\":\"subscription.updated\",\"payload\":{\"subscription\":\"oops\"}}")!;

        Assert.Null(hook.Subscription);
        Assert.Null(hook.Payment);
        Assert.Null(hook.CreatedAtUtc);
        Assert.False(hook.CreatedByThisPlatform);
    }
}
