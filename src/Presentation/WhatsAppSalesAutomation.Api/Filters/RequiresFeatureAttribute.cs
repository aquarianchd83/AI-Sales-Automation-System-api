using Microsoft.AspNetCore.Mvc.Filters;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Credits;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Filters;

/// <summary>
/// Refuses (HTTP 403, with the reason) every action of a controller to a tenant whose plan does not include the feature - the server-side half of hiding the
/// feature in the app, so a menu that is not shown can never be reached by typing its address. A request with no tenant (the platform admin) is not a
/// tenant's plan to check and passes.
///
/// A read (GET/HEAD) is let through once the tenant's free uses (or its plan's cap) of the feature are used up, so what it already made stays on screen;
/// anything that adds or changes (POST/PUT/PATCH/DELETE) still asks for the upgrade.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresFeatureAttribute : Attribute, IAsyncActionFilter
{
    public RequiresFeatureAttribute(PlatformFeature feature) => Feature = feature;

    public PlatformFeature Feature { get; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        var probe = context.ActionDescriptor.EndpointMetadata.OfType<SkipFeatureCheckAttribute>().Any();
        if (!probe && tenant.TenantId is { } tenantId)
        {
            var access = context.HttpContext.RequestServices.GetRequiredService<IFeatureAccessService>();
            var method = context.HttpContext.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
                await access.RequireViewAsync(tenantId, Feature, context.HttpContext.RequestAborted);
            else
                await access.RequireAsync(tenantId, Feature, context.HttpContext.RequestAborted);
        }

        await next();
    }
}

/// <summary>
/// Marks an action that answers "is this feature on for me?" itself, so the controller's feature check must not turn the answer into a 403 (which the app
/// would show the tenant as an error). The action must answer false instead.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipFeatureCheckAttribute : Attribute
{
}
