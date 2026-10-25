using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Consent;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Demo only: the SMS messages the simulated provider "sent" for the signed-in tenant, with their consent links, so the flow can be tried by
/// clicking instead of reading a phone. 404 unless ConsentPage:DemoOutboxEnabled is on; empty when a real SMS provider is in use.
/// </summary>
[ApiController]
[Route("api/v1/demo/sms-outbox")]
[Authorize(Roles = AppRoles.Admin)]
public class DemoSmsOutboxController : ControllerBase
{
    private static readonly Regex Link = new(@"https?://\S+", RegexOptions.Compiled);

    private readonly ISimulatedSmsOutbox _outbox;
    private readonly ITenantContext _tenant;
    private readonly ConsentPageOptions _options;

    public DemoSmsOutboxController(ISimulatedSmsOutbox outbox, ITenantContext tenant, IOptions<ConsentPageOptions> options)
    {
        _outbox = outbox;
        _tenant = tenant;
        _options = options.Value;
    }

    public record DemoSmsDto(DateTime SentAtUtc, string To, string Body, string? Link, string? Path);

    [HttpGet]
    public ActionResult<IReadOnlyList<DemoSmsDto>> Get()
    {
        if (!_options.DemoOutboxEnabled || _tenant.TenantId is not { } tenantId)
            return NotFound();

        Response.Headers.CacheControl = "no-store";
        var items = _outbox.List(tenantId).Select(m =>
        {
            var link = Link.Match(m.Body) is { Success: true } hit ? hit.Value : null;
            // "/consent/<token>" on its own, so the page can open it on whichever address the app is being served from.
            var path = link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri) ? uri.AbsolutePath : null;
            return new DemoSmsDto(m.SentAtUtc, m.ToPhoneE164, m.Body, link, path);
        }).ToList();
        return Ok(items);
    }
}
