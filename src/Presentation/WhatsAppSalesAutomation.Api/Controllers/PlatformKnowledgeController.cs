using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSalesAutomation.Application.Common.Interfaces;
using WhatsAppSalesAutomation.Application.KnowledgeBase.Retrieval;
using WhatsAppSalesAutomation.Domain.Constants;

namespace WhatsAppSalesAutomation.Api.Controllers;

/// <summary>
/// Knowledge administration for the platform team. PlatformSuperAdmin only, throughout: everything
/// here can read across tenants or shape what every tenant's support agent is allowed to say.
/// </summary>
[ApiController]
[Route("api/v1/platform/knowledge")]
[Authorize(Roles = AppRoles.PlatformSuperAdmin)]
public class PlatformKnowledgeController : ControllerBase
{
    private readonly IKnowledgeRetrievalSimulator _simulator;
    private readonly ICurrentUserService _currentUser;

    public PlatformKnowledgeController(IKnowledgeRetrievalSimulator simulator, ICurrentUserService currentUser)
    {
        _simulator = simulator;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Runs real retrieval for a question, optionally as a specific tenant, and returns every stage's
    /// breakdown: what was searched, what each leg found, how it fused, whether the reranker ran, and
    /// whether the evidence gate would have let the agent answer. See KnowledgeRetrievalSimulator.
    /// </summary>
    [HttpPost("retrieval/simulate")]
    public async Task<ActionResult<KnowledgeRetrievalResult>> Simulate(
        [FromBody] SimulateRetrievalRequest request, CancellationToken cancellationToken)
    {
        var actor = _currentUser.UserId ?? throw new InvalidOperationException("No authenticated user.");
        return Ok(await _simulator.SimulateAsync(request, actor, _currentUser.Email ?? string.Empty, cancellationToken));
    }
}
