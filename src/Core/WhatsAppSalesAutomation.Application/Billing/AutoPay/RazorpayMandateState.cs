using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Razorpay;
using WhatsAppSalesAutomation.Domain.Entities.Payments;

namespace WhatsAppSalesAutomation.Application.Billing.AutoPay;

/// <summary>
/// The one place Razorpay's picture of a subscription is copied onto our mandate row - whether it came from Razorpay's API or from a
/// webhook, the same rules apply. It moves the mandate record only: plan access and credits follow confirmed payments, never this.
/// </summary>
public static class RazorpayMandateState
{
    private static readonly RazorpaySubscriptionStatus[] EndedStates =
    {
        RazorpaySubscriptionStatus.Cancelled, RazorpaySubscriptionStatus.Completed, RazorpaySubscriptionStatus.Expired
    };

    /// <summary>Over for good. Razorpay never brings a cancelled, completed or expired subscription back, so nothing may either.</summary>
    public static IReadOnlyList<RazorpaySubscriptionStatus> Ended => EndedStates;

    public static bool IsEnded(RazorpaySubscriptionStatus status) => Array.IndexOf(EndedStates, status) >= 0;

    public static RazorpaySubscriptionStatus? ParseStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "created" => RazorpaySubscriptionStatus.Created,
        "authenticated" => RazorpaySubscriptionStatus.Authenticated,
        "active" => RazorpaySubscriptionStatus.Active,
        "pending" => RazorpaySubscriptionStatus.Pending,
        "halted" => RazorpaySubscriptionStatus.Halted,
        "cancelled" => RazorpaySubscriptionStatus.Cancelled,
        "completed" => RazorpaySubscriptionStatus.Completed,
        "expired" => RazorpaySubscriptionStatus.Expired,
        "paused" => RazorpaySubscriptionStatus.Paused,
        _ => null
    };

    /// <summary>Copies what Razorpay reports onto the row. False when Razorpay sent a status this code does not know: everything else is
    /// still taken, the status is left as it was, and the caller says so in the log.</summary>
    public static bool Apply(RazorpaySubscription row, RazorpaySubscriptionInfo info, DateTime nowUtc)
    {
        var known = ParseStatus(info.Status);
        if (known is { } status)
            row.Status = status;

        if (!string.IsNullOrEmpty(info.ShortUrl))
            row.ShortUrl = info.ShortUrl;
        if (info.TotalCount > 0)
            row.TotalCount = info.TotalCount;
        // Never backwards: a count Razorpay has reported is a payment it has taken.
        row.PaidCount = Math.Max(row.PaidCount, info.PaidCount);
        row.CurrentStartUtc = FromUnix(info.CurrentStart) ?? row.CurrentStartUtc;
        row.CurrentEndUtc = FromUnix(info.CurrentEnd) ?? row.CurrentEndUtc;
        row.ChargeAtUtc = FromUnix(info.ChargeAt);
        row.CustomerId = info.CustomerId ?? row.CustomerId;
        row.PaymentMethod = info.PaymentMethod ?? row.PaymentMethod;

        if (row.Status != RazorpaySubscriptionStatus.Created && row.Status != RazorpaySubscriptionStatus.Expired)
            row.AuthenticatedAtUtc ??= nowUtc;
        if (row.Status == RazorpaySubscriptionStatus.Active)
            row.ActivatedAtUtc ??= nowUtc;
        if (IsEnded(row.Status))
        {
            row.EndedAtUtc ??= FromUnix(info.EndedAt) ?? nowUtc;
            row.ChargeAtUtc = null;
            if (row.Status == RazorpaySubscriptionStatus.Cancelled)
                row.CancelledAtUtc ??= row.EndedAtUtc;
        }

        return known is not null;
    }

    public static void MarkEnded(RazorpaySubscription row, RazorpaySubscriptionStatus status, DateTime nowUtc)
    {
        row.Status = status;
        row.EndedAtUtc ??= nowUtc;
        row.ChargeAtUtc = null;
        if (status == RazorpaySubscriptionStatus.Cancelled)
            row.CancelledAtUtc ??= row.EndedAtUtc;
    }

    public static DateTime? FromUnix(long? seconds) =>
        seconds is { } s and > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
}

/// <summary>What AutoPay reads from the settings table: the operator's switch and the platform's Razorpay keys.</summary>
public record AutoPaySettings(bool Enabled, RazorpayCredentials? Credentials)
{
    /// <summary>The operator's switch, kept with the Razorpay keys. Off until someone turns it on: keys alone (the test page has them) must
    /// not start routing tenants to a mandate.</summary>
    public const string EnabledKey = "Razorpay:AutoPayEnabled";

    public static async Task<AutoPaySettings> ReadAsync(IAppSettingsStore store, CancellationToken cancellationToken = default)
    {
        var stored = await store.GetAllAsync(cancellationToken);
        string Get(string key) => stored.TryGetValue(key, out var value) ? value?.Trim() ?? string.Empty : string.Empty;

        var keyId = Get(RazorpayService.KeyIdKey);
        var secret = Get(RazorpayService.KeySecretKey);
        return new AutoPaySettings(
            string.Equals(Get(EnabledKey), "true", StringComparison.OrdinalIgnoreCase),
            keyId.Length > 0 && secret.Length > 0 ? new RazorpayCredentials(keyId, secret) : null);
    }
}
