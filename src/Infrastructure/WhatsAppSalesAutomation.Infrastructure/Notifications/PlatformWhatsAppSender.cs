using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Notifications;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure.Notifications;

/// <summary>
/// The platform's own WhatsApp number, as set on the Platform Admin Console's WhatsApp page ("PlatformWhatsApp" settings) - not any tenant's,
/// so a billing alert works when the tenant's own quota is zero and never spends it. Sends the notice templates and, for the same account,
/// creates and reviews them on Meta. With no credentials (or the number switched off) a send is Skipped rather than faked as sent: an alert
/// the tenant never received must not be recorded as delivered.
/// </summary>
public class PlatformWhatsAppSender : IPlatformWhatsAppSender, IPlatformWhatsAppTemplateAdmin
{
    private readonly MetaWhatsAppCloudApiClient _meta;
    private readonly IOptionsSnapshot<PlatformWhatsAppOptions> _options;
    private readonly ILogger<PlatformWhatsAppSender> _logger;

    public PlatformWhatsAppSender(MetaWhatsAppCloudApiClient meta, IOptionsSnapshot<PlatformWhatsAppOptions> options, ILogger<PlatformWhatsAppSender> logger)
    {
        _meta = meta;
        _options = options;
        _logger = logger;
    }

    private TenantWhatsAppCredentials? Credentials()
    {
        var o = _options.Value;
        if (!o.IsConfigured)
            return null;

        return new TenantWhatsAppCredentials(
            o.PhoneNumberId.Trim(), o.WhatsAppBusinessAccountId.Trim(), o.AccessToken.Trim(), string.Empty,
            string.IsNullOrWhiteSpace(o.ApiVersion) ? "v19.0" : o.ApiVersion.Trim(),
            string.IsNullOrWhiteSpace(o.ApiBaseUrl) ? "https://graph.facebook.com/" : o.ApiBaseUrl.Trim());
    }

    public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(Credentials() is not null);

    public async Task<DeliveryResult> SendTemplateAsync(
        string toPhoneE164, string templateName, string languageCode, IReadOnlyList<string> parameters,
        CancellationToken cancellationToken = default, string? mediaUrl = null)
    {
        var credentials = Credentials();
        if (credentials is null)
        {
            _logger.LogInformation("WhatsApp alert to {To} not sent: the platform WhatsApp number isn't configured", toPhoneE164);
            return new DeliveryResult(false, "platform WhatsApp number not configured", Skipped: true);
        }

        try
        {
            var result = await _meta.SendTemplateMessageAsync(credentials, toPhoneE164, templateName, languageCode, parameters, mediaUrl, cancellationToken);
            return new DeliveryResult(result.Success, result.Success ? null : result.ErrorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WhatsApp alert to {To} failed", toPhoneE164);
            return new DeliveryResult(false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var credentials = Credentials();
        return credentials is null ? Array.Empty<WhatsAppRemoteTemplate>() : await _meta.GetMessageTemplatesAsync(credentials, cancellationToken);
    }

    public async Task<WhatsAppTemplateSubmitResult> CreateTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        var credentials = Credentials();
        return credentials is null
            ? new WhatsAppTemplateSubmitResult(false, null, null, "The platform WhatsApp number is not configured.")
            : await _meta.CreateMessageTemplateAsync(credentials, submission, cancellationToken);
    }

    public async Task<WhatsAppTemplateSubmitResult> UpdateTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        var credentials = Credentials();
        return credentials is null
            ? new WhatsAppTemplateSubmitResult(false, metaTemplateId, null, "The platform WhatsApp number is not configured.")
            : await _meta.UpdateMessageTemplateAsync(credentials, metaTemplateId, submission, cancellationToken);
    }
}
