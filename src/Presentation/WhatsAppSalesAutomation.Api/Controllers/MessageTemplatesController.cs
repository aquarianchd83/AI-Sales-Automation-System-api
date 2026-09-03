using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.MessageTemplates;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/message-templates")]
[Authorize]
public class MessageTemplatesController : ControllerBase
{
    private readonly IMessageTemplateService _templateService;

    public MessageTemplatesController(IMessageTemplateService templateService)
    {
        _templateService = templateService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<MessageTemplateDto>>> GetPaged([FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _templateService.GetPagedAsync(request, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MessageTemplateDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _templateService.GetByIdAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<MessageTemplateDto>> Create([FromBody] CreateMessageTemplateRequest request, CancellationToken cancellationToken)
    {
        var result = await _templateService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MessageTemplateDto>> Update(Guid id, [FromBody] UpdateMessageTemplateRequest request, CancellationToken cancellationToken)
        => Ok(await _templateService.UpdateAsync(id, request, cancellationToken));

    /// <summary>A manual override, independent of the hourly MessageTemplateSyncJob - see
    /// <see cref="ReviewMessageTemplateRequest"/>.</summary>
    [HttpPost("{id:guid}/review")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<ActionResult<MessageTemplateDto>> Review(Guid id, [FromBody] ReviewMessageTemplateRequest request, CancellationToken cancellationToken)
        => Ok(await _templateService.ReviewAsync(id, request, cancellationToken));

    /// <summary>Runs the same push-then-pull reconciliation MessageTemplateSyncJob does hourly, on
    /// demand - useful right after creating/editing a template so it reaches Meta (and its resulting
    /// status comes back) without waiting for the next scheduled run.</summary>
    [HttpPost("sync")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<ActionResult<TemplateSyncResultDto>> Sync(CancellationToken cancellationToken)
        => Ok(await _templateService.SyncWithMetaAsync(cancellationToken));

    /// <summary>Same push-then-pull cycle as <see cref="Sync"/>, scoped to one template - the per-row
    /// "Sync" button on the Message Templates admin page.</summary>
    [HttpPost("{id:guid}/sync")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<ActionResult<MessageTemplateSyncOneResultDto>> SyncOne(Guid id, CancellationToken cancellationToken)
        => Ok(await _templateService.SyncOneAsync(id, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _templateService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
