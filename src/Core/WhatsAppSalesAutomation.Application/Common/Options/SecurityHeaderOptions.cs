namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from the "SecurityHeaders" config section. Applied by <c>SecurityHeadersMiddleware</c>.
///
/// This API serves three quite different things off one origin - JSON for the Angular app, the small
/// self-contained admin pages under wwwroot, and the Hangfire dashboard - so the headers here are the
/// conservative set that is correct for all three rather than the tight set that would only suit the
/// JSON. The one place that needs to differ is the Content-Security-Policy, which is why
/// <see cref="ContentSecurityPolicy"/> is configurable at all instead of hardcoded.
/// </summary>
public class SecurityHeaderOptions
{
    /// <summary>Master switch, on by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Emit <c>Strict-Transport-Security</c>. Off by default and turned on per environment,
    /// because HSTS is a promise a browser remembers for <see cref="HstsMaxAgeDays"/> - sending it
    /// from a local dev box teaches that browser to refuse plain HTTP to localhost for the next six
    /// months, which is a genuinely annoying thing to undo. Production appsettings turn it on.</summary>
    public bool EnableHsts { get; set; }

    public int HstsMaxAgeDays { get; set; } = 180;

    public bool HstsIncludeSubDomains { get; set; } = true;

    /// <summary>Send the <c>preload</c> directive. Separate from <see cref="HstsIncludeSubDomains"/>
    /// because preload is effectively irreversible - it asks browser vendors to ship the domain in a
    /// hardcoded list, and removal takes months. Opt in only once HTTPS is certain for every
    /// subdomain, forever.</summary>
    public bool HstsPreload { get; set; }

    /// <summary>Sent as-is. The default allows the wwwroot admin pages and the Hangfire dashboard to
    /// work (both use inline styles, and Hangfire uses inline script), while still blocking the thing
    /// that actually matters here: loading script from a third-party origin. <c>frame-ancestors
    /// 'none'</c> is the modern <c>X-Frame-Options: DENY</c> and is sent alongside it for older
    /// browsers.
    ///
    /// If the admin pages and Hangfire ever move off this origin, tighten this to
    /// <c>default-src 'none'; frame-ancestors 'none'</c> - a pure JSON API needs nothing else.</summary>
    public string ContentSecurityPolicy { get; set; } =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>Set empty to suppress the header entirely.</summary>
    public string ReferrerPolicy { get; set; } = "strict-origin-when-cross-origin";

    /// <summary>Turns off browser features this API has no use for, so a hypothetical injected script
    /// on the admin pages cannot reach for a camera or a geolocation prompt.</summary>
    public string PermissionsPolicy { get; set; } =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    /// <summary>Strip <c>Server</c> and <c>X-Powered-By</c> from responses. Not a real defence - it is
    /// version-hiding, not a fix - but it costs nothing and keeps the banner out of an automated
    /// scanner's report.</summary>
    public bool RemoveServerHeaders { get; set; } = true;
}
