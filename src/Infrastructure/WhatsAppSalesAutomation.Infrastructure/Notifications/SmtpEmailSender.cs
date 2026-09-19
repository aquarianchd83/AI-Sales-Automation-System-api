using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Notifications;

namespace WhatsAppSalesAutomation.Infrastructure.Notifications;

/// <summary>Bound from "Email:Smtp". With no Host configured the platform simply has no email channel: sends
/// come back Skipped, which the notification records as such rather than as a failure.</summary>
public class SmtpOptions
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string User { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public bool EnableSsl { get; set; } = true;
}

public class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DeliveryResult> SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host) || string.IsNullOrWhiteSpace(_options.From))
        {
            _logger.LogInformation("Email not sent to {To} ({Subject}): no SMTP host configured", toEmail, subject);
            return new DeliveryResult(false, "no SMTP host configured", Skipped: true);
        }

        try
        {
            using var client = new SmtpClient(_options.Host, _options.Port)
            {
                EnableSsl = _options.EnableSsl,
                Credentials = string.IsNullOrWhiteSpace(_options.User) ? null : new NetworkCredential(_options.User, _options.Password)
            };
            using var message = new MailMessage(_options.From, toEmail, subject, body);
            await client.SendMailAsync(message, cancellationToken);
            return new DeliveryResult(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Email to {To} failed", toEmail);
            return new DeliveryResult(false, ex.Message);
        }
    }
}
