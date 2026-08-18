using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.KnowledgeBase;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/knowledge-base")]
[Authorize]
public class KnowledgeBaseController : ControllerBase
{
    private readonly IKnowledgeBaseService _knowledgeBaseService;
    private readonly ICurrentUserService _currentUser;

    public KnowledgeBaseController(IKnowledgeBaseService knowledgeBaseService, ICurrentUserService currentUser)
    {
        _knowledgeBaseService = knowledgeBaseService;
        _currentUser = currentUser;
    }

    [HttpGet("articles")]
    public async Task<ActionResult<PagedResult<KnowledgeBaseArticleDto>>> GetPaged(
        [FromQuery] PagedRequest request, [FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(await _knowledgeBaseService.GetPagedAsync(request, status, cancellationToken));

    [HttpGet("articles/{id:guid}")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _knowledgeBaseService.GetByIdAsync(id, cancellationToken));

    [HttpPost("articles")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Create([FromBody] CreateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken)
    {
        var created = await _knowledgeBaseService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("articles/{id:guid}")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Update(Guid id, [FromBody] UpdateKnowledgeBaseArticleRequest request, CancellationToken cancellationToken)
        => Ok(await _knowledgeBaseService.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("articles/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _knowledgeBaseService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("articles/{id:guid}/publish")]
    public async Task<ActionResult<KnowledgeBaseArticleDto>> Publish(Guid id, CancellationToken cancellationToken)
    {
        var approvedByUserId = _currentUser.UserId ?? throw new InvalidOperationException("Authenticated request has no user id claim.");
        return Ok(await _knowledgeBaseService.PublishAsync(id, approvedByUserId, cancellationToken));
    }

    [HttpPost("reindex")]
    public async Task<IActionResult> Reindex(CancellationToken cancellationToken)
    {
        await _knowledgeBaseService.ReindexAsync(cancellationToken);
        return NoContent();
    }
}
