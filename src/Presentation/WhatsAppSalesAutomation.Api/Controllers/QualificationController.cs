using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Leads;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The tenant's qualification schema - what its AI sales agent tries to find out about a customer.
///
/// Admin-only for writes: the schema decides what every customer of this tenant gets asked, so it is
/// a business configuration rather than something an individual agent tunes. Reads are open to any
/// authenticated user of the tenant, since the lead screen shows captured values to whoever works the
/// lead.
/// </summary>
[ApiController]
[Route("api/v1/qualification")]
[Authorize]
public class QualificationController : ControllerBase
{
    private readonly IQualificationAdminService _qualification;
    private readonly ICurrentUserService _currentUser;
    private readonly ITenantContext _tenantContext;

    public QualificationController(
        IQualificationAdminService qualification,
        ICurrentUserService currentUser,
        ITenantContext tenantContext)
    {
        _qualification = qualification;
        _currentUser = currentUser;
        _tenantContext = tenantContext;
    }

    [HttpGet("fields")]
    public async Task<ActionResult<IReadOnlyList<QualificationFieldDto>>> GetFields(CancellationToken cancellationToken)
        => Ok(await _qualification.GetFieldsAsync(cancellationToken));

    [HttpGet("fields/{id:guid}")]
    public async Task<ActionResult<QualificationFieldDto>> GetField(Guid id, CancellationToken cancellationToken)
        => Ok(await _qualification.GetFieldAsync(id, cancellationToken));

    [HttpPost("fields")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<QualificationFieldDto>> CreateField(
        [FromBody] CreateQualificationFieldRequest request, CancellationToken cancellationToken)
    {
        var created = await _qualification.CreateFieldAsync(request, CurrentUserId(), cancellationToken);
        return CreatedAtAction(nameof(GetField), new { id = created.Id }, created);
    }

    [HttpPut("fields/{id:guid}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<QualificationFieldDto>> UpdateField(
        Guid id, [FromBody] UpdateQualificationFieldRequest request, CancellationToken cancellationToken)
        => Ok(await _qualification.UpdateFieldAsync(id, request, CurrentUserId(), cancellationToken));

    /// <summary>Takes a field out of the AI's questions (or puts it back) without touching values
    /// already captured for it - what a tenant almost always means by "remove this question".</summary>
    [HttpPost("fields/{id:guid}/active")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<QualificationFieldDto>> SetFieldActive(
        Guid id, [FromQuery] bool isActive, CancellationToken cancellationToken)
        => Ok(await _qualification.SetFieldActiveAsync(id, isActive, CurrentUserId(), cancellationToken));

    /// <summary>Refused with 409 when any lead has a value captured for this field - deactivate it
    /// instead. What customers told the business is not recoverable from a misclick.</summary>
    [HttpDelete("fields/{id:guid}")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> DeleteField(Guid id, CancellationToken cancellationToken)
    {
        await _qualification.DeleteFieldAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("fields/reorder")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<IReadOnlyList<QualificationFieldDto>>> ReorderFields(
        [FromBody] ReorderQualificationFieldsRequest request, CancellationToken cancellationToken)
        => Ok(await _qualification.ReorderFieldsAsync(request, CurrentUserId(), cancellationToken));

    /// <summary>Creates the default schema for a tenant that has none. Additive and idempotent, so it
    /// is safe to call from an onboarding screen's "start with the defaults" button.</summary>
    [HttpPost("fields/seed-defaults")]
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<ActionResult<IReadOnlyList<QualificationFieldDto>>> SeedDefaults(CancellationToken cancellationToken)
    {
        var tenantId = CurrentTenantId();
        return Ok(await _qualification.SeedDefaultsAsync(tenantId, cancellationToken));
    }

    [HttpGet("leads/{leadId:guid}")]
    public async Task<ActionResult<LeadQualificationDto>> GetLeadQualification(Guid leadId, CancellationToken cancellationToken)
        => Ok(await _qualification.GetLeadQualificationAsync(leadId, cancellationToken));

    /// <summary>An agent supplying or correcting a value. Stored as human-entered, which also stops
    /// the AI from asking for it again.</summary>
    [HttpPut("leads/{leadId:guid}/{fieldKey}")]
    public async Task<ActionResult<LeadQualificationDto>> SetLeadValue(
        Guid leadId, string fieldKey, [FromBody] SetLeadQualificationValueRequest request, CancellationToken cancellationToken)
        => Ok(await _qualification.SetLeadValueAsync(leadId, fieldKey, request, CurrentUserId(), cancellationToken));

    /// <summary>Clears a captured value so the agent asks for it again - the correction path for an
    /// extraction that was wrong and has no obvious right replacement.</summary>
    [HttpDelete("leads/{leadId:guid}/{fieldKey}")]
    public async Task<ActionResult<LeadQualificationDto>> ClearLeadValue(
        Guid leadId, string fieldKey, CancellationToken cancellationToken)
        => Ok(await _qualification.ClearLeadValueAsync(leadId, fieldKey, cancellationToken));

    private Guid CurrentUserId() =>
        _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");

    /// <summary>ITenantContext rather than the raw JWT claim - it is what the rest of the app
    /// consults, and it is also what the query filter these rows are written under reads.</summary>
    private Guid CurrentTenantId() =>
        _tenantContext.TenantId ?? throw new InvalidOperationException("Request has no tenant in scope.");
}
