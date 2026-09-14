using Microsoft.Extensions.Logging;

namespace WhatsAppSalesAutomation.Application.Common;

/// <summary>
/// Tags every log line written inside the scope with a tenant id - the <c>[t:...]</c> segment of the
/// Serilog output template, which is what the Platform Admin Console's Logs screen filters on.
///
/// An authenticated request is already tagged by the API's TenantLogEnricher off the JWT's tenant
/// claim. This exists for the units of work that have no such claim and only learn their tenant
/// partway through: a per-tenant background job run, and the anonymous WhatsApp webhook. Call it
/// right where <c>ITenantContext.SetTenant</c> is called and hold it with <c>using var</c> for the
/// rest of that method.
/// </summary>
public static class TenantLogScope
{
    public const string PropertyName = "TenantId";

    public static IDisposable? Begin(ILogger logger, Guid tenantId) =>
        logger.BeginScope(new Dictionary<string, object> { [PropertyName] = tenantId });
}
