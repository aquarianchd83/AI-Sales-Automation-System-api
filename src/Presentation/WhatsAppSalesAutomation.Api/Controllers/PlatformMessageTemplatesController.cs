using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>The platform's own WhatsApp notice templates - PlatformSuperAdmin-only. One per kind of notice, seeded at startup; the admin rewords
/// them, gives them an image, switches them off, and pushes them to Meta for review. Not the tenants' templates (/api/v1/templates).</summary>
[ApiController]
[Route("api/v1/platform/message-templates")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformMessageTemplatesController : ControllerBase
{
    private readonly IPlatformMessageTemplateService _templates;
    private readonly IPlatformAuditService _auditService;
    private readonly ICurrentUserService _currentUser;

    public PlatformMessageTemplatesController(IPlatformMessageTemplateService templates, IPlatformAuditService auditService, ICurrentUserService currentUser)
    {
        _templates = templates;
        _auditService = auditService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PlatformMessageTemplateDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _templates.GetAllAsync(cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PlatformMessageTemplateDto>> Update(Guid id, [FromBody] UpdatePlatformMessageTemplateRequest request, CancellationToken cancellationToken)
    {
        var result = await _templates.UpdateAsync(id, request, cancellationToken);
        await LogAsync(PlatformAuditActions.PlatformTemplateUpdated, $"Template \"{result.Name}\" ({result.EventKey}) edited; status {result.Status}, {(result.IsActive ? "on" : "off")}", cancellationToken);
        return Ok(result);
    }

    /// <summary>Puts the seeded wording back (an approved template goes back to review).</summary>
    [HttpPost("{id:guid}/restore-default")]
    public async Task<ActionResult<PlatformMessageTemplateDto>> RestoreDefault(Guid id, CancellationToken cancellationToken)
    {
        var result = await _templates.RestoreDefaultAsync(id, cancellationToken);
        await LogAsync(PlatformAuditActions.PlatformTemplateRestored, $"Template \"{result.Name}\" ({result.EventKey}) restored to its default text", cancellationToken);
        return Ok(result);
    }

    /// <summary>Pushes new and edited templates to Meta and pulls every template's review status. Always 200: the result says what happened.</summary>
    [HttpPost("sync")]
    public async Task<ActionResult<PlatformTemplateSyncResultDto>> Sync(CancellationToken cancellationToken)
    {
        var result = await _templates.SyncAsync(cancellationToken);
        if (result.Configured)
            await LogAsync(PlatformAuditActions.PlatformTemplatesSynced,
                $"Synced with Meta: {result.Created} created, {result.Updated} updated, {result.Failures.Count} failed, {result.StatusUpdated} status changes", cancellationToken);
        return Ok(result);
    }

    [HttpPost("{id:guid}/sync")]
    public async Task<ActionResult<PlatformMessageTemplateDto>> SyncOne(Guid id, CancellationToken cancellationToken)
        => Ok(await _templates.SyncOneAsync(id, cancellationToken));

    /// <summary>Sends the template with sample values to a number. Always 200: the result says whether it went.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult<DeliveryTestResultDto>> Test(Guid id, [FromBody] SendPlatformTemplateTestRequest request, CancellationToken cancellationToken)
        => Ok(await _templates.SendTestAsync(id, request.To, cancellationToken));

    private Task LogAsync(string action, string details, CancellationToken cancellationToken) =>
        _auditService.LogAsync(
            _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user."), _currentUser.Email ?? string.Empty, action,
            details: details, cancellationToken: cancellationToken);
}
