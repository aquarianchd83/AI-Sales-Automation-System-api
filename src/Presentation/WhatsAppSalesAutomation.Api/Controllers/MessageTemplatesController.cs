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

    /// <summary>Stands in for Meta's real review process - see <see cref="ReviewMessageTemplateRequest"/>.</summary>
    [HttpPost("{id:guid}/review")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<ActionResult<MessageTemplateDto>> Review(Guid id, [FromBody] ReviewMessageTemplateRequest request, CancellationToken cancellationToken)
        => Ok(await _templateService.ReviewAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _templateService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }
}
