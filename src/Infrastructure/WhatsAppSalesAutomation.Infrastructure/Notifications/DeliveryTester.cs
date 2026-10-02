using Microsoft.Extensions.Logging;
using WhatsAppSalesAutomation.Application.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Notifications;

public class DeliveryTester : IDeliveryTester
{
    private readonly HttpClient _http;
    private readonly ILogger<DeliveryTester> _logger;

    public DeliveryTester(HttpClient http, ILogger<DeliveryTester> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<DeliveryTestResultDto> SendEmailAsync(DeliveryEmailSettings settings, string toEmail, CancellationToken cancellationToken = default)
    {
        var options = new SmtpOptions
        {
            Host = settings.Host, Port = settings.Port, User = settings.User, Password = settings.Password,
            From = settings.From, EnableSsl = settings.EnableSsl
        };

        var result = await SmtpMailer.SendAsync(options, toEmail, "Test message from your sales platform",
            "This is a test email. If you can read it, the SMTP settings work.", _logger, cancellationToken);

        return result.Success
            ? new DeliveryTestResultDto(true, $"Test email sent to {toEmail}. Check the inbox (and spam).")
            : new DeliveryTestResultDto(false, $"The SMTP server did not accept the message: {result.Note}");
    }

    public async Task<DeliveryTestResultDto> SendSmsAsync(DeliverySmsSettings settings, string toPhoneE164, CancellationToken cancellationToken = default)
    {
        var options = new Msg91Options { Enabled = true, AuthKey = settings.AuthKey, OtpTemplateId = settings.OtpTemplateId, BaseUrl = settings.BaseUrl };

        // A sample code, so the registered template renders as it would for a real one.
        var result = await Msg91Client.SendOtpAsync(_http, options, toPhoneE164, "123456", _logger, cancellationToken);

        return result.Success
            ? new DeliveryTestResultDto(true, $"Test text sent to {toPhoneE164} (the code in it, 123456, is a sample).")
            : new DeliveryTestResultDto(false, $"MSG91 did not send the text: {result.Note}");
    }
}
