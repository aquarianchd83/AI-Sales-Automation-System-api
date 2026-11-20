using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>CRUD over the AI provider model catalogue (Google, Anthropic, OpenAI, ...). PlatformSuperAdmin only.</summary>
[ApiController]
[Route("api/v1/platform/ai-provider-models")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformAiProviderModelsController : ControllerBase
{
    private readonly IAiProviderModelService _models;
    private readonly ICurrentUserService _currentUser;

    public PlatformAiProviderModelsController(IAiProviderModelService models, ICurrentUserService currentUser)
    {
        _models = models;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AiProviderModelDto>>> GetAll([FromQuery] string? provider, [FromQuery] string? search, CancellationToken cancellationToken)
        => Ok(await _models.GetAllAsync(provider, search, cancellationToken));

    [HttpPost]
    public async Task<ActionResult<AiProviderModelDto>> Create([FromBody] SaveAiProviderModelRequest request, CancellationToken cancellationToken)
        => Ok(await _models.CreateAsync(request, ActorUserId, ActorEmail, cancellationToken));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AiProviderModelDto>> Update(Guid id, [FromBody] SaveAiProviderModelRequest request, CancellationToken cancellationToken)
        => Ok(await _models.UpdateAsync(id, request, ActorUserId, ActorEmail, cancellationToken));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _models.DeleteAsync(id, ActorUserId, ActorEmail, cancellationToken);
        return NoContent();
    }

    private Guid ActorUserId => _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
