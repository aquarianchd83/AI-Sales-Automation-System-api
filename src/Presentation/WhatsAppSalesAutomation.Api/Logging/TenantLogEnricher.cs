using Microsoft.AspNetCore.Http;
using Serilog.Core;
using Serilog.Events;
using WhatsAppSalesAutomation.Application.Common;
using WhatsAppSalesAutomation.Infrastructure.Identity;

namespace WhatsAppSalesAutomation.Api.Logging;

/// <summary>
/// Stamps each log event written during an authenticated request with that user's tenant id, read
/// straight off the JWT's tenant claim - so every line a tenant's request causes (a failed Meta send,
/// an unhandled exception caught by ExceptionHandlingMiddleware) is filterable by tenant on the
/// Platform Admin Console's Logs screen.
///
/// Reads the claim rather than resolving ITenantContext: that is a scoped service, and log events are
/// also written outside any request scope. Units of work with no tenant claim (background jobs, the
/// anonymous webhook) tag themselves via <see cref="TenantLogScope"/> instead; AddPropertyIfAbsent
/// means whichever of the two got there first wins, and they only ever agree.
/// </summary>
public class TenantLogEnricher : ILogEventEnricher
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantLogEnricher(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var value = _httpContextAccessor.HttpContext?.User?.FindFirst(JwtClaimNames.TenantId)?.Value;
        if (Guid.TryParse(value, out var tenantId))
            logEvent.AddPropertyIfAbsent(new LogEventProperty(TenantLogScope.PropertyName, new ScalarValue(tenantId)));
    }
}
