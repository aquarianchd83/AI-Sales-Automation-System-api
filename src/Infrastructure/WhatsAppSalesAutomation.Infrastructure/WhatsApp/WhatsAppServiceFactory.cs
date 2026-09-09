using WhatsAppSalesAutomation.Application.Common.Interfaces;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// The DI-registered <see cref="IWhatsAppService"/> - every consumer (CampaignSendService,
/// ConversationService, MessageTemplateService, MediaService, ...) keeps injecting IWhatsAppService
/// exactly as before; this class is what actually decides, per call, whether that means Meta or
/// Simulated. Provider *type* selection can no longer be a single DI-time choice once each tenant can
/// bring their own WABA (or none at all) independently - this factory is what replaces
/// DependencyInjection.AddWhatsAppClient's old startup-time "pick one implementation" switch.
///
/// Falls back to <see cref="SimulatedWhatsAppClient"/> - never throws - whenever the current tenant
/// has no connected WhatsApp config: a brand-new trial tenant that has not pasted in Meta credentials
/// yet, a PlatformSuperAdmin request, or (until Phase 3's per-tenant job loop lands) a recurring
/// Hangfire job running with no tenant in scope at all. This mirrors exactly what "Simulated" already
/// meant pre-multi-tenant - zero credentials needed - so nothing regresses for an unconfigured tenant;
/// it just quietly behaves like every tenant used to by default.
/// </summary>
public class WhatsAppServiceFactory : IWhatsAppService
{
    private readonly ITenantWhatsAppConfigProvider _configProvider;
    private readonly MetaWhatsAppCloudApiClient _metaClient;
    private readonly SimulatedWhatsAppClient _simulatedClient;

    public WhatsAppServiceFactory(
        ITenantWhatsAppConfigProvider configProvider, MetaWhatsAppCloudApiClient metaClient, SimulatedWhatsAppClient simulatedClient)
    {
        _configProvider = configProvider;
        _metaClient = metaClient;
        _simulatedClient = simulatedClient;
    }

    public async Task<WhatsAppSendResult> SendTemplateMessageAsync(
        string toPhoneNumberE164,
        string templateName,
        string languageCode,
        IReadOnlyList<string> parameterValues,
        string? mediaUrl = null,
        CancellationToken cancellationToken = default)
    {
        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.SendTemplateMessageAsync(toPhoneNumberE164, templateName, languageCode, parameterValues, mediaUrl, cancellationToken)
            : await _metaClient.SendTemplateMessageAsync(credentials, toPhoneNumberE164, templateName, languageCode, parameterValues, mediaUrl, cancellationToken);
    }

    public async Task<WhatsAppSendResult> SendTextMessageAsync(string toPhoneNumberE164, string text, CancellationToken cancellationToken = default)
    {
        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.SendTextMessageAsync(toPhoneNumberE164, text, cancellationToken)
            : await _metaClient.SendTextMessageAsync(credentials, toPhoneNumberE164, text, cancellationToken);
    }

    public async Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.UploadMediaAsync(content, contentType, cancellationToken)
            : await _metaClient.UploadMediaAsync(credentials, content, contentType, cancellationToken);
    }

    public async Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetMessageTemplatesAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.GetMessageTemplatesAsync(cancellationToken)
            : await _metaClient.GetMessageTemplatesAsync(credentials, cancellationToken);
    }

    public async Task<WhatsAppTemplateSubmitResult> CreateMessageTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.CreateMessageTemplateAsync(submission, cancellationToken)
            : await _metaClient.CreateMessageTemplateAsync(credentials, submission, cancellationToken);
    }

    public async Task<WhatsAppTemplateSubmitResult> UpdateMessageTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.UpdateMessageTemplateAsync(metaTemplateId, submission, cancellationToken)
            : await _metaClient.UpdateMessageTemplateAsync(credentials, metaTemplateId, submission, cancellationToken);
    }
}
