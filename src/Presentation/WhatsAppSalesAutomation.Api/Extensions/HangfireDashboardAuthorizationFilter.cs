using Hangfire.Dashboard;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Extensions;

/// <summary>
/// Gate for <c>/hangfire</c>. The dashboard is browser-navigated, not called with an Authorization
/// header the way the rest of this API's JWT bearer scheme expects - a real deployment needs either
/// a cookie-based admin session or an IP allowlist in front of this route before it is exposed.
/// Until then this denies everyone outside Development (where Swagger is similarly wide open), rather
/// than trusting an auth check that cannot actually see the caller's bearer token.
/// </summary>
public class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    private readonly bool _isDevelopment;

    public HangfireDashboardAuthorizationFilter(bool isDevelopment)
    {
        _isDevelopment = isDevelopment;
    }

    public bool Authorize(DashboardContext context)
    {
        if (_isDevelopment)
            return true;

        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true && httpContext.User.IsInRole(AppRoles.SuperAdmin);
    }
}
