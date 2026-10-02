using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Notifications;

namespace WhatsAppSalesAutomation.Infrastructure.Notifications;

/// <summary>Bound from "Email:Smtp", which the Platform Admin Console's Authentication Delivery page keeps in the AppSettings
/// table (the password encrypted). With no Host configured the platform simply has no email channel: sends come back
/// Skipped, which the notification records as such rather than as a failure.</summary>
public class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public bool EnableSsl { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

/// <summary>The one place a message actually goes out over SMTP. The sender below calls it with the saved settings, and the
/// Test button calls it with whatever is in the form - so a test exercises exactly what a real send would.</summary>
public static class SmtpMailer
{
    public static async Task<DeliveryResult> SendAsync(
        SmtpOptions options, string toEmail, string subject, string body, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (!options.IsConfigured)
        {
            logger.LogInformation("Email not sent to {To} ({Subject}): no SMTP host configured", toEmail, subject);
            return new DeliveryResult(false, "no SMTP host configured", Skipped: true);
        }

        try
        {
            using var client = new SmtpClient(options.Host, options.Port)
            {
                EnableSsl = options.EnableSsl,
                Credentials = string.IsNullOrWhiteSpace(options.User) ? null : new NetworkCredential(options.User, options.Password)
            };
            using var message = new MailMessage(options.From, toEmail, subject, body);
            await client.SendMailAsync(message, cancellationToken);
            return new DeliveryResult(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Email to {To} failed", toEmail);
            return new DeliveryResult(false, ex.Message);
        }
    }
}

public class SmtpEmailSender : IEmailSender
{
    // A snapshot, not IOptions: an administrator who saves new SMTP settings is not asked to restart anything.
    private readonly IOptionsSnapshot<SmtpOptions> _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptionsSnapshot<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<DeliveryResult> SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default) =>
        SmtpMailer.SendAsync(_options.Value, toEmail, subject, body, _logger, cancellationToken);
}
