using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Api.Filters;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.ContentStudio.Analytics;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// How the videos published from the AI Content Studio are doing. Admins and sales managers; every call works on the calling tenant only (another
/// tenant's id is "not found") and is refused unless the studio is on for the tenant. Read-only on the platform: it only asks for numbers.
/// </summary>
[ApiController]
[RequiresFeature(PlatformFeature.ContentStudio)]
[Route("api/v1/content-studio")]
[Authorize(Roles = StudioRoles.Contributors)]
public class ContentStudioAnalyticsController : ControllerBase
{
    private readonly IVideoAnalyticsService _analytics;
    private readonly ITenantContext _tenantContext;

    public ContentStudioAnalyticsController(IVideoAnalyticsService analytics, ITenantContext tenantContext)
    {
        _analytics = analytics;
        _tenantContext = tenantContext;
    }

    [HttpGet("analytics/summary")]
    public async Task<ActionResult<AnalyticsSummaryDto>> Summary(CancellationToken cancellationToken)
        => Ok(await _analytics.GetSummaryAsync(RequireTenantId(), cancellationToken));

    /// <summary>Asks the platform about one post now (not more often than every 15 minutes per post).</summary>
    [HttpPost("publications/{id:guid}/metrics/refresh")]
    public async Task<ActionResult<PublicationPerformanceDto>> Refresh(Guid id, CancellationToken cancellationToken)
        => Ok(await _analytics.RefreshAsync(RequireTenantId(), id, cancellationToken));

    private Guid RequireTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Authenticated content-studio request has no tenant in scope.");
}
