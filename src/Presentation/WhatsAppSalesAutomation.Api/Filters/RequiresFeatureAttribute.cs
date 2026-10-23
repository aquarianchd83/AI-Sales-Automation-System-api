using Microsoft.AspNetCore.Mvc.Filters;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Filters;

/// <summary>
/// Refuses (HTTP 403, with the reason) every action of a controller to a tenant whose plan does not include the feature - the server-side half of hiding the
/// feature in the app, so a menu that is not shown can never be reached by typing its address. A request with no tenant (the platform admin) is not a
/// tenant's plan to check and passes.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresFeatureAttribute : Attribute, IAsyncActionFilter
{
    public RequiresFeatureAttribute(PlatformFeature feature) => Feature = feature;

    public PlatformFeature Feature { get; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        if (tenant.TenantId is { } tenantId)
        {
            var access = context.HttpContext.RequestServices.GetRequiredService<IFeatureAccessService>();
            await access.RequireAsync(tenantId, Feature, context.HttpContext.RequestAborted);
        }

        await next();
    }
}
