using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.Common.Models;
using WhatsAppSalesAutomation.Application.Platform;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// What the AI screens add to the existing provider settings: per provider, the last Verify result, declared versus verified capabilities and a
/// measured accuracy test (on Configuration &gt; AI Providers); and the product-knowledge AI policy and the AI call log (on Integrations). The
/// providers' own settings - On/Off, key, model, prices - are edited only through the settings API, as before. PlatformSuperAdmin only.
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

    [HttpPost("{provider}/benchmark")]
    public async Task<ActionResult<AiProviderInsightDto>> Benchmark(string provider, CancellationToken cancellationToken)
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
