using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Tags;

namespace WhatsAppSalesAutomation.Api.Controllers;

[ApiController]
[Route("api/v1/tags")]
[Authorize]
public class TagsController : ControllerBase
{
    private readonly ITagService _tagService;

    public TagsController(ITagService tagService)
    {
        _tagService = tagService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<TagDto>>> GetPaged([FromQuery] PagedRequest request, CancellationToken cancellationToken)
        => Ok(await _tagService.GetPagedAsync(request, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TagDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _tagService.GetByIdAsync(id, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<TagDto>> Create([FromBody] CreateTagRequest request, CancellationToken cancellationToken)
    {
        var result = await _tagService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = result.Id }, result);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<TagDto>> Update(Guid id, [FromBody] UpdateTagRequest request, CancellationToken cancellationToken)
        => Ok(await _tagService.UpdateAsync(id, request, cancellationToken));

    /// <summary>
    /// Deleting a tag that is still applied to customers returns 409 with the count; pass
    /// <c>?force=true</c> to delete it and drop those assignments.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] bool force, CancellationToken cancellationToken)
    {
        await _tagService.DeleteAsync(id, force, cancellationToken);
        return NoContent();
    }
}
