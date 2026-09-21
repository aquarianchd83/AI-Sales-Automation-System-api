namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from the "RateLimiting" config section. Governs ASP.NET Core's built-in rate limiter, wired
/// up in <c>RateLimitingServiceExtensions</c>.
///
/// Three buckets rather than one global number, because the three kinds of traffic this API takes
/// fail in completely different ways. <see cref="Auth"/> is about credential stuffing against
/// <c>/api/v1/auth/login</c> - a handful of attempts a minute is generous for a human and useless for
/// a dictionary. <see cref="Webhook"/> is about Meta replaying a burst of delivery receipts at us -
/// it must be high enough that legitimate fan-out is never dropped, because a 429 to Meta is a lost
/// message, not a retried one. <see cref="Api"/> is the ordinary authenticated surface, partitioned
/// per user so one tenant's runaway script cannot starve another's.
///
/// Deliberately NOT in <c>AppSettingCatalog</c> (so not editable from the Settings screen): a limiter
/// that the thing being limited can raise is not a limiter. Changing these is a deployment.
/// </summary>
public class RateLimitOptions
{
    /// <summary>Master switch. On by default - an unprotected auth endpoint is the exact hole this
    /// exists to close, so the safe default is "on" and turning it off is the deliberate act. Local
    /// development that trips limits while hammering an endpoint can set it false in
    /// appsettings.Development.json.</summary>
    public bool Enabled { get; set; } = true;

    public RateLimitRule Auth { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    public RateLimitRule Webhook { get; set; } = new() { PermitLimit = 600, WindowSeconds = 60 };

    public RateLimitRule Api { get; set; } = new() { PermitLimit = 300, WindowSeconds = 60 };

    /// <summary>Client IPs exempt from every bucket - health checkers, an uptime monitor, or the
    /// office egress IP during a load test. Matched against the connection's remote IP after
    /// <c>UseForwardedHeaders</c> has run, so behind the ngrok/reverse-proxy setup described in
    /// Program.cs this is the real caller, not the proxy.</summary>
    public string[] ExemptIpAddresses { get; set; } = Array.Empty<string>();
}

/// <summary>One fixed-window bucket: <see cref="PermitLimit"/> requests per
/// <see cref="WindowSeconds"/>, per partition.
///
/// Fixed window rather than sliding or token bucket on purpose. A sliding window costs memory
/// proportional to request count per partition, and with a per-IP partition on a public endpoint that
/// is an attacker-controlled allocation. The fixed window's known weakness - up to 2x the limit
/// across a window boundary - is irrelevant at these magnitudes: 20 login attempts in two seconds is
/// still nowhere near enough to brute-force anything.</summary>
public class RateLimitRule
{
    public int PermitLimit { get; set; }

    public int WindowSeconds { get; set; }

    /// <summary>How many requests over the limit are held and served as the window rolls, rather than
    /// rejected outright. Zero by default: queueing a rejected login attempt only delays telling the
    /// caller no, and holds a connection open while doing it.</summary>
    public int QueueLimit { get; set; }
}
