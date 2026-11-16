using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Razorpay;

namespace WhatsAppSalesAutomation.Tests;

/// <summary>The AppSettings table in memory, for the Razorpay keys and the AutoPay switch.</summary>
public sealed class MemorySettingsStore : IAppSettingsStore
{
    public Dictionary<string, string?> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>(Rows, StringComparer.OrdinalIgnoreCase));

    public Task UpsertAsync(IReadOnlyDictionary<string, string?> values, Guid? updatedByUserId, CancellationToken cancellationToken = default)
    {
        foreach (var (key, value) in values)
            Rows[key] = value;
        return Task.CompletedTask;
    }

    public Task ReplacePrefixesAsync(IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> prefixes, Guid? updatedByUserId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}

/// <summary>
/// Razorpay's plans and subscriptions in memory. A test plays Razorpay's part by changing <see cref="Subscriptions"/> (the customer authorised
/// it, a cycle was charged) and can make any call fail with Razorpay's own kind of message. Records every call, so a test can assert that
/// something was - or, just as important, was not - asked of Razorpay.
/// </summary>
public sealed class FakeSubscriptionGateway : IRazorpayGateway
{
    private int _plans;
    private int _subscriptions;

    public Dictionary<string, RazorpayPlanInfo> Plans { get; } = new();
    public Dictionary<string, RazorpaySubscriptionInfo> Subscriptions { get; } = new();

    /// <summary>Razorpay's invoices, by invoice id. One per billing cycle of a subscription.</summary>
    public Dictionary<string, RazorpayInvoiceInfo> Invoices { get; } = new();
    public string? InvoiceError { get; set; }
    public List<string> InvoiceListCalls { get; } = new();
    public List<string> InvoiceCalls { get; } = new();

    public string? CreatePlanError { get; set; }
    public string? CreateSubscriptionError { get; set; }
    public string? FetchError { get; set; }
    public string? CancelError { get; set; }

    /// <summary>What Razorpay "really" created, when it differs from what was asked - a gateway that misbehaves.</summary>
    public long? PlanAmountOverride { get; set; }

    /// <summary>Runs just before a subscription is created - lets a test slip something in between the checks and the create.</summary>
    public Action? BeforeCreateSubscription { get; set; }

    public List<(string Period, int Interval, string Name, long Amount, string Currency)> PlanCalls { get; } = new();
    public List<(string PlanId, int TotalCount, bool CustomerNotify, long? ExpireBy, IReadOnlyDictionary<string, string> Notes, RazorpayCredentials Credentials)> SubscriptionCalls { get; } = new();
    public List<string> FetchCalls { get; } = new();
    public List<(string Id, bool AtCycleEnd)> CancelCalls { get; } = new();

    public Task<RazorpayResult<RazorpayPlanInfo>> CreatePlanAsync(
        RazorpayCredentials credentials, string period, int interval, string name, long amountMinor, string currency,
        IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default)
    {
        PlanCalls.Add((period, interval, name, amountMinor, currency));
        if (CreatePlanError is not null)
            return Task.FromResult(RazorpayResult<RazorpayPlanInfo>.Fail(CreatePlanError));

        var plan = new RazorpayPlanInfo($"plan_T{++_plans:D6}", period, interval, PlanAmountOverride ?? amountMinor, currency);
        Plans[plan.Id] = plan;
        return Task.FromResult(RazorpayResult<RazorpayPlanInfo>.Ok(plan));
    }

    public Task<RazorpayResult<RazorpaySubscriptionInfo>> CreateSubscriptionAsync(
        RazorpayCredentials credentials, string planId, int totalCount, bool customerNotify, long? expireByUnix,
        IReadOnlyDictionary<string, string> notes, CancellationToken cancellationToken = default)
    {
        BeforeCreateSubscription?.Invoke();
        SubscriptionCalls.Add((planId, totalCount, customerNotify, expireByUnix, notes, credentials));
        if (CreateSubscriptionError is not null)
            return Task.FromResult(RazorpayResult<RazorpaySubscriptionInfo>.Fail(CreateSubscriptionError));

        var id = $"sub_T{++_subscriptions:D6}";
        var subscription = new RazorpaySubscriptionInfo(
            id, planId, "created", $"https://rzp.io/i/{id}", totalCount, 0, totalCount, null, null, null, null, null, null, null, null);
        Subscriptions[id] = subscription;
        return Task.FromResult(RazorpayResult<RazorpaySubscriptionInfo>.Ok(subscription));
    }

    public Task<RazorpayResult<RazorpaySubscriptionInfo>> FetchSubscriptionAsync(
        RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default)
    {
        FetchCalls.Add(subscriptionId);
        if (FetchError is not null)
            return Task.FromResult(RazorpayResult<RazorpaySubscriptionInfo>.Fail(FetchError));
        return Task.FromResult(Subscriptions.TryGetValue(subscriptionId, out var s)
            ? RazorpayResult<RazorpaySubscriptionInfo>.Ok(s)
            : RazorpayResult<RazorpaySubscriptionInfo>.Fail("The id provided does not exist"));
    }

    public Task<RazorpayResult<RazorpaySubscriptionInfo>> CancelSubscriptionAsync(
        RazorpayCredentials credentials, string subscriptionId, bool cancelAtCycleEnd, CancellationToken cancellationToken = default)
    {
        CancelCalls.Add((subscriptionId, cancelAtCycleEnd));
        if (CancelError is not null)
            return Task.FromResult(RazorpayResult<RazorpaySubscriptionInfo>.Fail(CancelError));
        if (!Subscriptions.TryGetValue(subscriptionId, out var s))
            return Task.FromResult(RazorpayResult<RazorpaySubscriptionInfo>.Fail("The id provided does not exist"));

        // Like Razorpay: at cycle end it stays as it is until the cycle is over; otherwise it is cancelled on the spot.
        if (!cancelAtCycleEnd)
            s = s with { Status = "cancelled", EndedAt = 1_800_000_000, ChargeAt = null };
        Subscriptions[subscriptionId] = s;
        return Task.FromResult(RazorpayResult<RazorpaySubscriptionInfo>.Ok(s));
    }

    public Task<RazorpayResult<IReadOnlyList<RazorpayInvoiceInfo>>> FetchSubscriptionInvoicesAsync(
        RazorpayCredentials credentials, string subscriptionId, CancellationToken cancellationToken = default)
    {
        InvoiceListCalls.Add(subscriptionId);
        return Task.FromResult(InvoiceError is not null
            ? RazorpayResult<IReadOnlyList<RazorpayInvoiceInfo>>.Fail(InvoiceError)
            : RazorpayResult<IReadOnlyList<RazorpayInvoiceInfo>>.Ok(Invoices.Values.Where(i => i.SubscriptionId == subscriptionId).ToList()));
    }

    public Task<RazorpayResult<RazorpayInvoiceInfo>> FetchInvoiceAsync(
        RazorpayCredentials credentials, string invoiceId, CancellationToken cancellationToken = default)
    {
        InvoiceCalls.Add(invoiceId);
        if (InvoiceError is not null)
            return Task.FromResult(RazorpayResult<RazorpayInvoiceInfo>.Fail(InvoiceError));
        return Task.FromResult(Invoices.TryGetValue(invoiceId, out var i)
            ? RazorpayResult<RazorpayInvoiceInfo>.Ok(i)
            : RazorpayResult<RazorpayInvoiceInfo>.Fail("The id provided does not exist"));
    }

    // Orders, payments and refunds belong to the test page - see RazorpayServiceTests.
    public Task<RazorpayResult<bool>> PingAsync(RazorpayCredentials credentials, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RazorpayResult<RazorpayOrderInfo>> CreateOrderAsync(
        RazorpayCredentials credentials, long amountMinor, string currency, string receipt, IReadOnlyDictionary<string, string> notes,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RazorpayResult<RazorpayPaymentInfo>> FetchPaymentAsync(RazorpayCredentials credentials, string paymentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RazorpayResult<RazorpayPaymentInfo>> CapturePaymentAsync(
        RazorpayCredentials credentials, string paymentId, long amountMinor, string currency, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<RazorpayResult<RazorpayRefundInfo>> RefundAsync(RazorpayCredentials credentials, string paymentId, long? amountMinor, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
