using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Content Management System - the FAQ screen. <see cref="GetPublished"/> is open to any authenticated
/// user of any tenant; every other action is PlatformSuperAdmin-only.
/// </summary>
[ApiController]
[Route("api/v1/platform/faq")]
[Authorize]
public class PlatformFaqController : ControllerBase
{
    private readonly IFaqService _faqService;
    private readonly ICurrentUserService _currentUser;

    public PlatformFaqController(IFaqService faqService, ICurrentUserService currentUser)
    {
        _faqService = faqService;
        _currentUser = currentUser;
    }

    [HttpGet("published")]
    public async Task<ActionResult<IReadOnlyList<FaqEntryDto>>> GetPublished(CancellationToken cancellationToken)
        => Ok(await _faqService.GetPublishedAsync(cancellationToken));

    [HttpGet]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<IReadOnlyList<FaqEntryDto>>> GetAll(CancellationToken cancellationToken)
        => Ok(await _faqService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<FaqEntryDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _faqService.GetByIdAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<FaqEntryDto>> Create([FromBody] CreateFaqEntryRequest request, CancellationToken cancellationToken)
    {
        var created = await _faqService.CreateAsync(request, ActorUserId, ActorEmail, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<ActionResult<FaqEntryDto>> Update(Guid id, [FromBody] UpdateFaqEntryRequest request, CancellationToken cancellationToken)
        => Ok(await _faqService.UpdateAsync(id, request, ActorUserId, ActorEmail, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = AppRoles.PlatformSuperAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _faqService.DeleteAsync(id, ActorUserId, ActorEmail, cancellationToken);
        return NoContent();
    }

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
