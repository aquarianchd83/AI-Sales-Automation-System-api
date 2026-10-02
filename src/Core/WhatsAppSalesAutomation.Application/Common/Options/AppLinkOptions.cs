namespace WhatsAppSalesAutomation.Application.Common.Options;

/// <summary>
/// Bound from the "App" config section. Where the web app lives, for the links the API puts in emails
/// (password reset, email verification).
///
/// Configured, never taken from the incoming request: a link built from the Host or Origin header can be
/// pointed at an attacker's site by whoever asks for the email, which turns "forgot password" into a way
/// to steal the reset token. With no value set, those emails are simply not sent.
/// </summary>
public class AppLinkOptions
{
    /// <summary>The web app's public address, no trailing slash needed, e.g. "https://app.example.com".</summary>
    public string PublicUrl { get; set; } = string.Empty;
}
