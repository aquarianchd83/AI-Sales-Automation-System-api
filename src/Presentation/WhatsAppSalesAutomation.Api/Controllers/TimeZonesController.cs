using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Tenancy;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The public timezone catalog - backs the signup page's picker, the tenant's own
/// Workspace settings editor, and the Platform Admin Console's tenant detail override, all built
/// from the same list so none of them can drift out of sync with each other. See
/// TimeZoneCatalog's own doc comment for why this is a curated list, not the host OS's own
/// TimeZoneInfo.GetSystemTimeZones().</summary>
[ApiController]
[Route("api/v1/timezones")]
[AllowAnonymous]
public class TimeZonesController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<TimeZoneOption>> Get() => Ok(TimeZoneCatalog.All);
}
