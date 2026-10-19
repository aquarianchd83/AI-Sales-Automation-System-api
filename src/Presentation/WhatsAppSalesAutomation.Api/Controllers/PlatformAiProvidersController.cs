using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// The Integrations page's AI provider cards: Simulator, ChatGPT, Claude and Gemini - on/off, key, model, Verify Connection, status,
/// capabilities, price, measured accuracy and cost, fallback policy and the execution log. PlatformSuperAdmin only: no tenant ever reaches
/// provider configuration, and a key is write-only (it is never returned, only a masked hint). Every change is audited.
/// </summary>
[ApiController]
[Route("api/v1/platform/ai-providers")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformAiProvidersController : ControllerBase
{
    private readonly IPlatformAiProvidersService _providers;
    private readonly ICurrentUserService _currentUser;

    public PlatformAiProvidersController(IPlatformAiProvidersService providers, ICurrentUserService currentUser)
    {
        _providers = providers;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<AiProvidersOverviewDto>> Get(CancellationToken cancellationToken)
        => Ok(await _providers.GetAsync(cancellationToken));

    [HttpPut("{provider}")]
    public async Task<ActionResult<AiProviderPanelDto>> Update(string provider, [FromBody] UpdateAiProviderRequest request, CancellationToken cancellationToken)
        => Ok(await _providers.UpdateProviderAsync(provider, request, ActorId, ActorEmail, cancellationToken));

    [HttpPost("{provider}/verify")]
    public async Task<ActionResult<AiProviderCheckResultDto>> Verify(string provider, CancellationToken cancellationToken)
        => Ok(await _providers.VerifyAsync(provider, ActorId, ActorEmail, cancellationToken));

    [HttpPost("{provider}/benchmark")]
    public async Task<ActionResult<AiProviderPanelDto>> Benchmark(string provider, CancellationToken cancellationToken)
        => Ok(await _providers.RunBenchmarkAsync(provider, ActorId, ActorEmail, cancellationToken));

    [HttpPut("policy")]
    public async Task<ActionResult<AiPolicyDto>> UpdatePolicy([FromBody] UpdateAiPolicyRequest request, CancellationToken cancellationToken)
        => Ok(await _providers.UpdatePolicyAsync(request, ActorId, ActorEmail, cancellationToken));

    [HttpGet("executions")]
    public async Task<ActionResult<PagedResult<AiExecutionDto>>> Executions([FromQuery] AiExecutionQuery query, CancellationToken cancellationToken)
        => Ok(await _providers.ListExecutionsAsync(query, cancellationToken));

    private Guid ActorId => _currentUser.UserId ?? throw new InvalidOperationException("A platform action needs a signed-in user.");

    private string ActorEmail => _currentUser.Email ?? string.Empty;
}
