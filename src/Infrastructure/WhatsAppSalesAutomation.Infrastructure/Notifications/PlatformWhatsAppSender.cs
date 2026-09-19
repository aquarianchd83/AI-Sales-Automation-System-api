using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure.Notifications;

/// <summary>
/// Sends from the platform's own WhatsApp number - the "WhatsApp" settings block, not any tenant's - so a
/// billing alert works when the tenant's own quota is zero and never spends it. With no platform Meta
/// credentials (Provider not "Meta", or no phone number id / token) the channel is Skipped rather than faked
/// as sent: an alert the tenant never received must not be recorded as delivered.
/// </summary>
public class PlatformWhatsAppSender : IPlatformWhatsAppSender
{
    private readonly MetaWhatsAppCloudApiClient _meta;
    private readonly IOptionsSnapshot<WhatsAppSettings> _settings;
    private readonly ILogger<PlatformWhatsAppSender> _logger;

    public PlatformWhatsAppSender(MetaWhatsAppCloudApiClient meta, IOptionsSnapshot<WhatsAppSettings> settings, ILogger<PlatformWhatsAppSender> logger)
    {
        _meta = meta;
        _settings = settings;
        _logger = logger;
    }

    public async Task<DeliveryResult> SendTemplateAsync(string toPhoneE164, string templateName, string languageCode, IReadOnlyList<string> parameters, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Value;
        if (!string.Equals(settings.Provider, "Meta", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(settings.PhoneNumberId)
            || string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            _logger.LogInformation("WhatsApp alert to {To} not sent: the platform WhatsApp number isn't configured", toPhoneE164);
            return new DeliveryResult(false, "platform WhatsApp number not configured", Skipped: true);
        }

        var credentials = new TenantWhatsAppCredentials(
            settings.PhoneNumberId, settings.WhatsAppBusinessAccountId, settings.AccessToken,
            settings.AppSecret, settings.ApiVersion, settings.ApiBaseUrl);

        try
        {
            var result = await _meta.SendTemplateMessageAsync(credentials, toPhoneE164, templateName, languageCode, parameters, null, cancellationToken);
            return new DeliveryResult(result.Success, result.Success ? null : result.ErrorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WhatsApp alert to {To} failed", toPhoneE164);
            return new DeliveryResult(false, ex.Message);
        }
    }
}
