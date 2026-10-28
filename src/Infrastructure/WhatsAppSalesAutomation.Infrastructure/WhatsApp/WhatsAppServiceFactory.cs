using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Application.WhatsAppBridge;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>
/// The DI-registered <see cref="IWhatsAppService"/> - every consumer (CampaignSendService,
/// ConversationService, MessageTemplateService, MediaService, ...) keeps injecting IWhatsAppService
/// exactly as before; this class is what actually decides, per call, whether that means Meta, the QR
/// bridge or Simulated. Provider *type* selection can no longer be a single DI-time choice once each tenant can
/// bring their own WABA (or none at all) independently - this factory is what replaces
/// DependencyInjection.AddWhatsAppClient's old startup-time "pick one implementation" switch.
///
/// Falls back to <see cref="SimulatedWhatsAppClient"/> - never throws - whenever the current tenant
/// has no connected WhatsApp config: a brand-new trial tenant that has not pasted in Meta credentials
/// yet, a PlatformSuperAdmin request, or (until Phase 3's per-tenant job loop lands) a recurring
/// Hangfire job running with no tenant in scope at all. This mirrors exactly what "Simulated" already
/// meant pre-multi-tenant - zero credentials needed - so nothing regresses for an unconfigured tenant;
/// it just quietly behaves like every tenant used to by default.
///
/// The QR bridge is chosen only when the platform's active provider is "QrBridge" AND the tenant is in the pilot list (or the list is
/// empty). For such a tenant there is NO fallback: a channel that is not connected yields a failed send, never a Meta or Simulated one,
/// because the original request may already have been accepted and a silent reroute could send the same message twice.
/// </summary>
public class WhatsAppServiceFactory : IWhatsAppService
{
    private readonly ITenantWhatsAppConfigProvider _configProvider;
    private readonly MetaWhatsAppCloudApiClient _metaClient;
    private readonly SimulatedWhatsAppClient _simulatedClient;
    private readonly ITenantContext _tenantContext;
    private readonly IOptionsSnapshot<WhatsAppProviderOptions> _providerOptions;
    private readonly IOptionsSnapshot<WhatsAppBridgeOptions> _bridgeOptions;
    private readonly IWhatsAppChannelStore _channels;
    private readonly IWhatsAppBridgeClient _bridgeClient;
    private readonly IApplicationDbContext _context;
    private readonly ILogger<WhatsAppServiceFactory> _logger;

    public WhatsAppServiceFactory(
        ITenantWhatsAppConfigProvider configProvider, MetaWhatsAppCloudApiClient metaClient, SimulatedWhatsAppClient simulatedClient,
        ITenantContext tenantContext, IOptionsSnapshot<WhatsAppProviderOptions> providerOptions, IOptionsSnapshot<WhatsAppBridgeOptions> bridgeOptions,
        IWhatsAppChannelStore channels, IWhatsAppBridgeClient bridgeClient, IApplicationDbContext context, ILogger<WhatsAppServiceFactory> logger)
    {
        _configProvider = configProvider;
        _metaClient = metaClient;
        _simulatedClient = simulatedClient;
        _tenantContext = tenantContext;
        _providerOptions = providerOptions;
        _bridgeOptions = bridgeOptions;
        _channels = channels;
        _bridgeClient = bridgeClient;
        _context = context;
        _logger = logger;
    }

    /// <summary>The tenant to route through the bridge, or null when this call is not a bridge call at all.</summary>
    private Guid? BridgeTenant()
    {
        if (_tenantContext.TenantId is not { } tenantId)
            return null;

        var provider = WhatsAppProviderRouting.Resolve(
            _providerOptions.Value.Active, _providerOptions.Value.TenantOverrides, _bridgeOptions.Value.PilotTenantIds, tenantId);
        return provider == WhatsAppProviders.QrBridge ? tenantId : null;
    }

    private async Task<(BridgeEndpoint? Endpoint, WhatsAppChannelRecord? Channel, string? Problem)> ResolveBridgeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var options = _bridgeOptions.Value;
        if (options.BaseUrl.Length == 0 || options.ApiKey.Length == 0)
            return (null, null, "The WhatsApp QR bridge is not configured. Sending is paused.");

        var channel = await _channels.GetAsync(tenantId, cancellationToken);
        if (channel is null || !BridgeStatuses.CanSend(channel.Status))
        {
            _logger.LogWarning("Send paused for tenant {TenantId}: QR channel is {Status}", tenantId, channel?.Status ?? BridgeStatuses.NotConnected);
            return (null, null, "This tenant's WhatsApp QR connection is not connected. Sending is paused until it is reconnected.");
        }

        return (new BridgeEndpoint(options.BaseUrl.TrimEnd('/'), options.ApiKey), channel, null);
    }

    public async Task<WhatsAppSendResult> SendTemplateMessageAsync(
        string toPhoneNumberE164,
        string templateName,
        string languageCode,
        IReadOnlyList<string> parameterValues,
        string? mediaUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (BridgeTenant() is { } bridgeTenant)
        {
            // The bridge has no templates: the template's own text is filled in here and sent as plain text.
            var template = await _context.MessageTemplates.AsNoTracking()
                .FirstOrDefaultAsync(t => t.WhatsAppTemplateName == templateName || t.Name == templateName, cancellationToken);
            if (template is null)
                return WhatsAppSendResult.Failed($"The template '{templateName}' was not found, so its text could not be sent through the QR bridge.");

            return await SendBridgeTextAsync(bridgeTenant, toPhoneNumberE164, TemplatePlaceholderResolver.FillPositional(template.BodyText, parameterValues), cancellationToken);
        }

        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.SendTemplateMessageAsync(toPhoneNumberE164, templateName, languageCode, parameterValues, mediaUrl, cancellationToken)
            : await _metaClient.SendTemplateMessageAsync(credentials, toPhoneNumberE164, templateName, languageCode, parameterValues, mediaUrl, cancellationToken);
    }

    public async Task<WhatsAppSendResult> SendTextMessageAsync(string toPhoneNumberE164, string text, CancellationToken cancellationToken = default)
    {
        if (BridgeTenant() is { } bridgeTenant)
            return await SendBridgeTextAsync(bridgeTenant, toPhoneNumberE164, text, cancellationToken);

        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.SendTextMessageAsync(toPhoneNumberE164, text, cancellationToken)
            : await _metaClient.SendTextMessageAsync(credentials, toPhoneNumberE164, text, cancellationToken);
    }

    private async Task<WhatsAppSendResult> SendBridgeTextAsync(Guid tenantId, string toPhoneNumberE164, string text, CancellationToken cancellationToken)
    {
        var (endpoint, channel, problem) = await ResolveBridgeAsync(tenantId, cancellationToken);
        return endpoint is null || channel is null
            ? WhatsAppSendResult.Failed(problem!)
            : await _bridgeClient.SendTextAsync(endpoint, channel.InstanceName, toPhoneNumberE164, text, cancellationToken);
    }

    public async Task<string> UploadMediaAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        if (BridgeTenant() is not null)
            throw new NotSupportedException("Media is not supported through the WhatsApp QR bridge yet.");

        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.UploadMediaAsync(content, contentType, cancellationToken)
            : await _metaClient.UploadMediaAsync(credentials, content, contentType, cancellationToken);
    }

    public async Task<IReadOnlyList<WhatsAppRemoteTemplate>> GetMessageTemplatesAsync(CancellationToken cancellationToken = default)
    {
        // Template review is a Meta concept; a bridge tenant has nothing to pull.
        if (BridgeTenant() is not null)
            return Array.Empty<WhatsAppRemoteTemplate>();

        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.GetMessageTemplatesAsync(cancellationToken)
            : await _metaClient.GetMessageTemplatesAsync(credentials, cancellationToken);
    }

    public async Task<WhatsAppTemplateSubmitResult> CreateMessageTemplateAsync(WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        if (BridgeTenant() is not null)
            return new WhatsAppTemplateSubmitResult(false, null, null, "Templates are not registered with the QR bridge.");

        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.CreateMessageTemplateAsync(submission, cancellationToken)
            : await _metaClient.CreateMessageTemplateAsync(credentials, submission, cancellationToken);
    }

    public async Task<WhatsAppTemplateSubmitResult> UpdateMessageTemplateAsync(string metaTemplateId, WhatsAppTemplateSubmission submission, CancellationToken cancellationToken = default)
    {
        if (BridgeTenant() is not null)
            return new WhatsAppTemplateSubmitResult(false, null, null, "Templates are not registered with the QR bridge.");

        var credentials = await _configProvider.GetForCurrentTenantAsync(cancellationToken);
        return credentials is null
            ? await _simulatedClient.UpdateMessageTemplateAsync(metaTemplateId, submission, cancellationToken)
            : await _metaClient.UpdateMessageTemplateAsync(credentials, metaTemplateId, submission, cancellationToken);
    }
}
