using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.WhatsApp;

/// <summary>Uses the same provider routing as <see cref="WhatsAppServiceFactory"/>, so "is this tenant on the QR bridge" has one answer.</summary>
public class TemplateSendPolicy : ITemplateSendPolicy
{
    private readonly ITenantContext _tenantContext;
    private readonly IOptionsSnapshot<WhatsAppProviderOptions> _providerOptions;
    private readonly IOptionsSnapshot<WhatsAppBridgeOptions> _bridgeOptions;

    public TemplateSendPolicy(
        ITenantContext tenantContext, IOptionsSnapshot<WhatsAppProviderOptions> providerOptions, IOptionsSnapshot<WhatsAppBridgeOptions> bridgeOptions)
    {
        _tenantContext = tenantContext;
        _providerOptions = providerOptions;
        _bridgeOptions = bridgeOptions;
    }

    public bool IsSendable(MessageTemplate template)
    {
        if (!template.IsActive)
            return false;

        var provider = WhatsAppProviderRouting.Resolve(
            _providerOptions.Value.Active, _providerOptions.Value.TenantOverrides, _bridgeOptions.Value.PilotTenantIds, _tenantContext.TenantId);
        return provider == WhatsAppProviders.QrBridge || template.WhatsAppTemplateStatus == WhatsAppTemplateStatus.Approved;
    }
}
